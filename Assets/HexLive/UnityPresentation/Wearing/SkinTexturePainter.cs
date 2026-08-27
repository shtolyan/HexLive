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
    /// URP Lit. Tan/sunburn/grime is baked into the paint target's BASE layer
    /// (SetSkinTone → SkinTintBlit) so it sits UNDER the wound/bandage stamps —
    /// the marks keep their true colour and NpcActorView pins _BaseColor white
    /// on painted slots (un-painted skin still gets the cheap _BaseColor tint).
    /// Clothing occludes it all naturally.
    /// </summary>
    public sealed class SkinTexturePainter : MonoBehaviour, IPaintTarget
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
            // The simulation tracks the source for medical accounting; the
            // renderer uses the same gauze artwork for every dressing.
            public bool IsGauze;
            // Spec 40.8 v4 water droplet: the effect stamp (refraction normal
            // + rim + coverage/halo) and the atlas cell both textures use.
            public Texture? Effect;
            public Rect CellRect = new(0f, 0f, 1f, 1f);
            public bool IsDroplet;
            // Spec 40.8-H zone-damage speckle: albedo-only (every other
            // texture stays null, so the normal/gloss walks skip it) and
            // drawn FIRST in RepaintSlot — wounds/bandages land on top.
            public bool IsSpeckle;
            // §40.8-H r11: синяк — та же спекл-частица, но фиолетовая версия
            // арта и прозрачнее. Источник — BluntDamage зоны (тупой урон),
            // рисуется САМЫМ НИЖНИМ (глубже крови: разлитое под кожей, не на
            // ней). Albedo-only, как спеклы.
            public bool IsBruise;
            // §118.2: кровь, проступившая СКВОЗЬ повязку. Рисуется отдельным
            // проходом ПОСЛЕ обмотки — иначе порядок словаря решал бы, видно её
            // или нет, а бинт кладётся на весь тайл и перекрыл бы пятно.
            public bool IsBleed;
            // r4: seeded random spin (degrees) applied via the GL matrix at
            // draw time, so the same splatter art never tiles visibly.
            public float RotationDeg;

            // ---- Spec 40.8-J: seam-free projected stamp ----
            // A projected stamp is not a UV rectangle at all: it is a box in
            // BIND (mesh) space, and every texel whose baked body point falls
            // inside it gets painted — on whatever island, tile or texture
            // that texel belongs to. That is what lets a hip wrap lie half on
            // the leg and half on the torso instead of being scissored at the
            // edge of the Legs tile.
            public bool IsProjected;
            // Bind space -> decal space (xy in [-0.5,0.5], z in mesh units).
            public Matrix4x4 ObjectToDecal;
            // Same for the 1.6x blood-splash underlay.
            public Matrix4x4 UnderToDecal;
            public Vector3 BindPos;
            public Vector3 DecalNormal;
            public float Depth;        // half-thickness of the accepted slab
            // EVERY slot the decal reaches. Painting is per-slot (each slot
            // owns its render target), so a stamp that crosses a texture
            // boundary is drawn once per side.
            public int[]? Slots;
            // The UV window to sweep on each of those slots — index-aligned
            // with Slots. Resolved ONCE here: it depends only on the decal and
            // the baked cell grid, and re-deriving it per draw call cost a
            // 1024-cell scan every time (three per stamp per slot, four times
            // a second, during a fight — pure waste).
            public Rect[]? Windows;
            public Rect[]? UnderWindows;
            // Slots/Windows are computed on a worker thread (they are the
            // heavy part of placing a stamp and they land right on the frame a
            // hit is taken). Nothing paints until this flips true.
            public volatile bool GeometryReady;
            // Spec 40.8-K fresh lane: draw the plain rectangle stamp for now
            // (Uv/UvSizeX/UvSizeY are filled even on the projected path), and
            // switch to the seam-free projection on the next scheduled cycle.
            public bool DrawAsRect;
            // Set by the worker when the decal reaches more than one submesh.
            public bool NeedsSeamUpgrade;
        }

        // 1024 visibly softened the 4096 Daz skin (the whole slot swaps to the
        // paint target on the first wound) — 2048 keeps the pores readable.
        private const int MaxRenderTextureSize = 2048;

        // PERF (profiling, Aug-2026): texture memory measured 1.08 GB with 73
        // live render textures, and this class is the source — three targets per
        // material slot per girl. Only the ALBEDO carries detail a player reads
        // (tan, grime, the wound art itself), so it keeps the full 2048. The
        // NORMAL is wound relief — low-frequency bumps under a stamp — and the
        // GLOSS is a smoothness mask, softer still. Each halving is 4× the
        // memory, and at these frequencies neither shows the difference.
        private const int MaxNormalRenderTextureSize = 1024;
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
        // The gloss mask is soft — the same reasoning as the normal target above
        // (1024 already cost ~5 MB per slot for a smoothness ramp; 512 is still
        // finer than the ramp itself).
        private const int GlossRtSize = 512;

        // ---- wound volume knobs (spec 40.8-D v5) ----
        // Fresh cuts glisten: absolute smoothness stamped into the wet core
        // (base skin stays at the caller's dry/wet value, 0.32 dry).
        // Do NOT use 1.0: zero roughness collapses URP's GGX highlight to a
        // sub-pixel point, so some wounds look matte unless sun/view alignment
        // is exact. 0.92 is still far above wet skin (0.72), but spreads a
        // readable highlight across every wound at gameplay distance.
        private const float WoundWetGloss = 0.92f;

        // A LITTLE surface relief on the wound (v5 cut wound relief for UV-seam
        // ridge artifacts — but a flat smooth-1 surface only mirrors a
        // highlight at the exact angle, which is why the wet gloss never read
        // as "shiny". Dirt reads shiny precisely because its normal map
        // scatters glints across viewing angles). Re-added SUBTLY and separate
        // from the heal fade so seam flares stay faint; 0 restores the fully
        // flat v5 wound. ~half of dirt's 0.7 normal blend.
        private const float WoundReliefStrength = 0.35f;

        // ---- Spec 40.8-H zone-damage speckle knobs ----
        // A zone whose HP is low grows a field of small blood/bruise blots
        // UNDER the wound art — near 0 HP the limb reads almost fully covered.
        // First marks appear almost immediately (r2: the v1 onset of 0.15 +
        // ease-in curve read as "very little blood" — the user's target is
        // the OLD full-damage density already at ~15% damage, ~5× at full).
        private const float SpeckleOnsetDamage = 0.05f;
        // A damaged zone tints its neighbours at this fraction of its own
        // damage, so a shattered arm bleeds a few blots onto the shoulder
        // instead of a hard red-arm/white-torso border.
        private const float SpeckleNeighborBleed = 0.38f;
        private const float SpeckleAlphaMin = 0.45f;
        private const float SpeckleAlphaMax = 1f;
        // World-metre blot size at full 1.7 m rig scale. r4: ×5 over r3 —
        // each stamp is a full palm-to-forearm-sized splatter; they freely
        // overlap (centres stay unique via the grid walk, nothing else is
        // deduplicated), which is what builds the solid gore near zero HP.
        // r7: SMALLER blots, four times as many. At 25-45 cm a single blot was
        // the largest decal in the game — bigger than a bandage — and it is
        // drawn as a RECTANGLE in one submesh's UV, so it physically cannot
        // cross a seam: the spine line on a bloodied back was one blot ending
        // at the island edge. Blood is irregular, so a small blot cut at a
        // seam reads as "the splatter ends there", while a palm-sized one
        // reads as a straight cut. Coverage is preserved by count, since a
        // blot's area goes as the SQUARE of its size: 0.35 m -> 0.16 m mean is
        // a quarter of the area, so the ceilings below are x4.
        private const float SpeckleWorldSizeMin = 0.12f;
        private const float SpeckleWorldSizeMax = 0.20f;
        // Speckles get their own UV cap: the droplet 0.12 face-tile
        // insurance would strangle these big splatters. 0.5 still keeps a
        // single stamp from swallowing a whole dense UV tile.
        // r7: was 0.5 (half a tile) to let the old palm-sized blots through.
        // The small ones never need that much, and the cap still saves a dense
        // face tile from a blown-up stamp.
        private const float SpeckleMaxUvSize = 0.22f;
        // Grid-cell stride for placement: odd => coprime with the 8×16 = 128
        // cell PaintPointMap grid, so consecutive indices walk a full cycle
        // with no duplicate cells and the fill grows evenly with damage.
        // r8 tried to keep blots away from UV island edges so the seam could
        // not slice them. REVERTED: the forbidden cells are a ring along each
        // island's border, and that ring is a different share of every zone —
        // 0% of the torso but 45% of an arm — so blood bunched into the middle
        // of every limb while the torso stayed even. Blots that found no legal
        // cell were dropped outright, thinning exactly the zones that rejected
        // most. An uneven body reads worse than a seam, so placement is back to
        // the plain walk: one cell per blot, even everywhere, and a growing
        // count never moves the blots already placed.
        private const int SpeckleCellStride = 45;
        // §40.8-H r11: the bruise field walks the same grid with its OWN stride
        // as well as its own start — sharing the stride would drop bruise #i on
        // blood blot #i once the starts happened to align. 27 is likewise
        // coprime with the 128-cell grid.
        private const int BruiseCellStride = 27;
        // r3: neutral — the stain art (the molly damage-decal splatter) is
        // already a rich saturated red and "классно смотрится" as-is, so the
        // stamp draws it unmodified instead of the r1 muted-bruise darkening.
        private static Color SpeckleTint(float alpha) => StampTint(alpha);
        // r6: NO solid flood — the r5 ceiling of 120 read as "перебор,
        // некрасиво". User calibration: the ~18-splatter torso look sits at
        // 80% damage, and 100% is only ~20% past it (≈22). So the ceiling
        // IS ~22 and the curve is near-linear: f(q(0.8)) = 18/22 → q^0.85.
        // Low damage still opens with a lone big blot.
        private static float SpeckleRamp(float q) => Mathf.Pow(q, 0.85f);

        // r6: ceilings scaled to the no-flood calibration (≈0.18 of r4).
        // r7 ceilings: x4 of the r6 calibration, to hold the same amount of
        // red now that each blot covers a quarter of the area. All stay under
        // the zone grid's 128 cells, so the coprime stride walk still gives
        // every blot its own centre.
        private static int MaxSpecklesFor(string zone) => zone switch
        {
            "Torso" => 88,
            "Pelvis" => 60,
            "LegL" or "LegR" => 52,
            "ArmL" or "ArmR" => 36,
            "Head" => 28,
            _ => 28
        };

        private static int SpeckleCountFor(string zone, float q) =>
            q <= 0f ? 0 : Mathf.Max(1, Mathf.RoundToInt(MaxSpecklesFor(zone) * SpeckleRamp(q)));

        // ---- §40.8-H r11: bruise (тупой урон) knobs ----
        // Синяки — те же спекл-частицы, но: арт перекрашен в багрово-фиолетовый
        // (MakeBruiseTexture), заметно прозрачнее (разлитое ПОД кожей, не
        // кровь на ней), чуть крупнее и вдвое реже — сплошная заливка читалась
        // бы как краска, а не как побои. Без neighbour-bleed: синяк локален —
        // где ударили, там и цветёт. Источник — BluntDamage зоны, поэтому
        // поле само сходит по мере заживления (TickBluntRecovery), воде тут
        // делать нечего.
        private const float BruiseAlphaMin = 0.22f;
        private const float BruiseAlphaMax = 0.5f;
        private const float BruiseWorldSizeMin = 0.16f;
        private const float BruiseWorldSizeMax = 0.28f;
        private static Color BruiseTint(float alpha) => StampTint(alpha);

        private static int BruiseCountFor(string zone, float q) =>
            q <= 0f ? 0 : Mathf.Max(1, Mathf.RoundToInt(MaxSpecklesFor(zone) * 0.5f * SpeckleRamp(q)));

        // Which zones a zone's damage bleeds onto (both directions listed).
        private static readonly Dictionary<string, string[]> SpeckleAdjacency = new()
        {
            ["Head"] = new[] { "Torso" },
            ["Torso"] = new[] { "Head", "Pelvis", "ArmL", "ArmR" },
            ["Pelvis"] = new[] { "Torso", "LegL", "LegR" },
            ["ArmL"] = new[] { "Torso" },
            ["ArmR"] = new[] { "Torso" },
            ["LegL"] = new[] { "Pelvis" },
            ["LegR"] = new[] { "Pelvis" },
        };

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
        private static readonly int SkinTintColorId = Shader.PropertyToID("_TintColor");
        private static readonly int PosMapId = Shader.PropertyToID("_PosMap");
        private static readonly int NrmMapId = Shader.PropertyToID("_NrmMap");
        private static readonly int ObjectToDecalId = Shader.PropertyToID("_ObjectToDecal");
        private static readonly int DecalNormalId = Shader.PropertyToID("_DecalNormal");
        private static readonly int DepthId = Shader.PropertyToID("_Depth");
        private static readonly int DepthFeatherId = Shader.PropertyToID("_DepthFeather");
        private static readonly int UnderToDecalId = Shader.PropertyToID("_UnderToDecal");
        private static readonly int UnderFadeId = Shader.PropertyToID("_UnderFade");
        private static readonly int UnderDepthId = Shader.PropertyToID("_UnderDepth");
        private static readonly int UnderDepthFeatherId = Shader.PropertyToID("_UnderDepthFeather");

        // ---- Spec 40.8-J projected-decal knobs ----
        // The accepted slab's half-thickness as a fraction of the decal's
        // longest side. Thin enough that the far side of a limb never gets a
        // mirrored copy, thick enough to follow the hip's curvature.
        private const float ProjectedDepthFactor = 0.4f;
        private const float ProjectedDepthFeather = 0.35f;
        // Reach margin when deciding which slots a decal touches, on top of
        // the baked sample spacing — a decal that only clips the corner of a
        // slot must still claim it, or that sliver goes unpainted. Kept small
        // on purpose: every slot claimed costs a ~21 MB render target, so the
        // reach is measured against the DETAILED art, not the faint 1.6x
        // blood halo around it (a clipped halo is invisible, a clipped wound
        // is the bug this whole path exists to fix).
        private const float ProjectedReachMargin = 0.005f;

        // Stamp art loads once per session, not once per wound.
        // NOTE: the RVFX pack splatters were tried as underlay variants and
        // reverted — on the BODY the original pair (the user's blood_splash
        // picture + the generated art) reads better; the pack textures serve
        // the ground stains and the splash VFX instead.
        private static bool _stampTexturesLoaded;
        private static Texture2D? _texSplash;
        private static Texture2D? _texScratch;
        private static Texture2D? _texSplat;
        // Spec 40.8-H r3: the zone-damage speckle art — the red splatter the
        // molly damage decal used (copied back from molly_copy as
        // blood_stain.png; the user asked for exactly this picture).
        private static Texture2D? _texStain;
        // §40.8-H r11: тот же сплаттер, перекрашенный в фиолетовый на лету
        // (MakeBruiseTexture) — отдельного PNG нет нарочно, форма синяка
        // обязана совпадать с формой кровяного пятна.
        private static Texture2D? _texBruise;
        private static Texture2D? _texBandage;
        private static Texture2D? _texGauze;

        // §118.2: фиксированная обмотка на зону, в UV её тайла. Ключ — имя зоны
        // из Zones ("Torso", "ArmL", …). Развёртка у всех актёров одна, поэтому
        // кэш статический: шесть картинок на весь проект, а не на колонистку.
        // Значение может быть null — это ЗАПОМНЕННОЕ отсутствие (у Head обмотки
        // нет), иначе Resources.Load дёргался бы каждую перерисовку.
        private static readonly Dictionary<string, Texture2D?> _wrapOverlays = new();
        private static readonly Dictionary<string, Texture2D?> _wrapNormals = new();

        // §118.2: клякса, проступающая сквозь повязку.
        private static Texture2D? _texBleed;

        // Сколько секунд СИЛЬНОГО кровотечения даёт пятно во всю ширину. Кровь
        // идёт не мгновенно, и пятно должно расти на глазах у игрока, а не
        // появляться готовым: полторы минуты — это заметный, но не мгновенный
        // рост при полной ране.
        private const float BloodSoakSeconds = 90f;

        // Ступени роста. Пятно перерисовывается ТОЛЬКО при смене ступени —
        // размер входит в ключ штампа, поэтому шесть ступеней = шесть перекладок за
        // всё кровотечение вместо перекладки каждый кадр (§40.8-K: медленная
        // половина картинки живёт в своём, редком ритме).
        private const int BloodSoakSteps = 6;

        // Накопленная промоклость по сиду раны, 0..1. Живёт между вызовами
        // Sync: это ИСТОРИЯ кровотечения, её нельзя пересчитать из кадра.
        private readonly Dictionary<int, float> _soak = new();

        // Что положить на следующем PlaceNewStamps: ключ -> (зона, сид, ступень).
        private readonly Dictionary<string, (string zone, int seed, int step)> _bleedPending = new();
        private readonly Dictionary<string, (string zone, int seed)> _plasterPending = new();
        private readonly HashSet<int> _soakSweep = new();
        private readonly List<int> _soakDrop = new();

        // ⭐ АВАРИЙНЫЙ ВЫКЛЮЧАТЕЛЬ обмотки. false = вернуться на старый круглый
        // бинт, который работает.
        //
        // Полнотайловый штамп обмотки стирает АЛЬФУ всего тайла: Graphics.
        // DrawTexture пишет RGBA, а квад накрывает весь таргет, поэтому за
        // пределами полосы альфа кожи уходит в ноль. Тайл Legs общий на обе
        // ноги — отсюда «одна повязка, а прозрачные обе». Чинится не размером
        // штампа, а тем, что этот проход не должен трогать альфу назначения
        // (ColorMask RGB), — но до проверки в игре обмотка выключена, чтобы не
        // держать редактор в сломанном виде.
        private const bool WrapOverlayEnabled = true;

        // ⭐ §118.2: ТУГОЙ прямоугольник каждой обмотки в UV её тайла
        // (центр u, центр v, ширина, высота), снят с альфы самой карты.
        //
        // Обмотка кладётся ОБЫЧНЫМ штампом, как рана или грязь, — а не квадом
        // на весь тайл, как было. Полнотайловый квад и был причиной «одна
        // повязка, а прозрачные обе ноги»: он накрывал весь общий тайл Legs.
        // Здесь же LegL живёт на u=0.748, а LegR на u=0.252 — разные половины,
        // и достать одну ногу штампом другой невозможно по построению.
        private static readonly Dictionary<string, Rect> WrapRects = new()
        {
            ["Torso"] = new Rect(0.50000f, 0.53467f, 0.71289f, 0.12402f),
            ["Pelvis"] = new Rect(0.50000f, 0.24072f, 0.85156f, 0.17871f),
            ["ArmL"] = new Rect(0.31836f, 0.76855f, 0.26953f, 0.37305f),
            ["ArmR"] = new Rect(0.68701f, 0.27930f, 0.26855f, 0.37305f),
            ["LegL"] = new Rect(0.74805f, 0.65674f, 0.41016f, 0.35645f),
            ["LegR"] = new Rect(0.25195f, 0.65674f, 0.41016f, 0.35645f),
        };

        private static Texture2D? WrapOverlayFor(string zoneName)
        {
            if (!WrapOverlayEnabled)
            {
                return null;
            }

            if (_wrapOverlays.TryGetValue(zoneName, out var cached))
            {
                return cached;
            }

            var tex = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>($"HexLive/Decals/bandage_wrap_{zoneName}");
            // ⚠ AtomicResources.Load ленивый: первый вызов стартует загрузку и
            // отдаёт null. Кэшировать этот null навсегда — значит до конца
            // сессии рисовать бинт старой круглой нашлёпкой («пластырь» вместо
            // обмотки). Кэшируется только доехавшая текстура; промах — ретрай.
            if (tex != null)
            {
                _wrapOverlays[zoneName] = tex;
            }
            return tex;
        }

        // §118.2: рельеф марли. Карта высот — яркость самой ткани, поэтому блик
        // идёт ровно по тем ниткам, которые видно в альбедо. Без неё обмотка
        // была бы плоской наклейкой: глубину в игре даёт ТОЛЬКО этот канал.
        private static Texture2D? WrapNormalFor(string zoneName)
        {
            if (_wrapNormals.TryGetValue(zoneName, out var cached))
            {
                return cached;
            }

            var tex = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>($"HexLive/Decals/bandage_wrap_{zoneName}_n");
            // Та же ленивая ловушка, что у WrapOverlayFor: null не кэшировать,
            // иначе рельеф марли навсегда остаётся плоским.
            if (tex != null)
            {
                _wrapNormals[zoneName] = tex;
            }
            return tex;
        }
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
        // Optional matching relief per variant (_n). Only scratch/splat ship
        // one; the gash variants fall back to the generic blood-bead normal
        // (_texSplatN) in WoundVariant so every wound still beads a little.
        private static Texture2D?[] _woundNormal = System.Array.Empty<Texture2D?>();
        // Decodes the (possibly DXT5nm) authored skin normal into plain RGB
        // before stamps blend on top (NormalDecodeBlit.shader).
        private static Material? _normalDecode;
        // Multiplies the authored skin albedo by the current tan/sunburn/grime
        // tone when laying the paint target's BASE layer, so the tan lives
        // UNDER the wound/bandage stamps (SkinTintBlit.shader). Wounds then
        // stamp on top at true colour; NpcActorView pins _BaseColor white on
        // painted slots so the tone isn't multiplied in twice.
        private static Material? _skinTintBlit;
        // Stamps wound wet-gloss into the map's alpha (WoundGlossStamp.shader:
        // BlendOp Max, ColorMask A — overlaps keep the shiniest value).
        private static Material? _glossStamp;
        // Spec 40.8 v4: composites a droplet's refraction/darkening/rim into
        // the albedo and its smoothness into the gloss map (DropletStamp.shader).
        private static Material? _dropletStamp;
        private static bool _dropletShaderWarned;
        // Spec 40.8-J: paints one decal by asking every texel "which body
        // point do you cover?" instead of filling a UV rectangle
        // (ProjectedStamp.shader) — the seam-free path.
        private static Material? _projectedStamp;
        private static bool _projectedShaderWarned;

        private SkinnedMeshRenderer? _body;
        private BodyBones? _bones;
        private Transform? _bodyRoot;
        private int _npcId;
        private float _height = 1.7f;
        private HashSet<int> _skinSlots = new();
        // Stable, compact iteration/capture form of _skinSlots. The renderer
        // often has 15-20 material slots but only 4-7 skin slots; repainting
        // never needs to rescan clothing, eyes, lashes, etc.
        private int[] _paintSlots = System.Array.Empty<int>();
        // Spec 40.8-G: editor-baked placement points — when present, wound/
        // droplet placement is a table lookup and BakeMesh never runs.
        private PaintPointMap? _map;
        // Spec 40.8-J: per-texel body positions — when present (and the point
        // map is v2), wounds and wraps paint through the seam-free projected
        // path instead of a per-slot rectangle.
        private SkinPositionMapSet? _posMaps;

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
        // True while a slot's _BaseMap carries painted albedo (wounds/bandages/
        // droplets) — the cue NpcActorView reads to pin that slot's _BaseColor
        // white (the tan is baked into the texture there, see _skinTone).
        private bool[] _albedoLive = System.Array.Empty<bool>();
        // Smoothness of wet-but-undropped skin — the gloss map's base value,
        // fed by NpcActorView from the unified wetness pool.
        private float _wetSmoothness = 0.32f;
        // Current tan/sunburn/grime tone (NpcActorView.SkinTint). Multiplied
        // into the paint target's base skin so wounds/bandages sit on a tanned
        // body without being tanned themselves. White = no weathering.
        private Color _skinTone = Color.white;

        // Spec 40.8-K: the tone currently BAKED INTO the paint targets, which
        // lags _skinTone until the Tone layer comes due.
        //
        // The composite is stacked bottom-up — tinted base, then the speckle
        // field, then wounds and bandages, then droplets — so the tan is the
        // BOTTOM layer and there is no way to re-tint it without redrawing
        // everything above. That makes a tan step the single most expensive
        // trigger in the painter, and it is also the least urgent thing on the
        // body. Holding it here is what makes the cap mean something: until it
        // expires, new wounds keep compositing over the OLD base (cheap,
        // additive), instead of every drifting tan step dragging a full
        // rebuild along with the first wound that follows it.
        private Color _appliedTone = Color.white;

        private readonly Dictionary<string, Stamp> _stamps = new();
        private readonly Dictionary<string, float> _alpha = new(); // key -> current fade
        private readonly HashSet<string> _desired = new();
        private readonly List<string> _stale = new();
        private int _lastStateHash;

        // ---- Spec 40.8-K: a flat cycle, spread out, plus a fresh lane ----
        //
        // Every painter is rebuilt on a fixed cycle (SkinPaintScheduler), and
        // the scheduler spreads the turns so only one rebuild happens at a
        // time. There is no per-reason bookkeeping: whatever changed, the next
        // turn redraws the whole composite. The one thing that cannot wait ten
        // seconds is a mark that JUST appeared — a bite, a dressing — so those
        // are drawn immediately through the cheap rectangle path and upgraded
        // to the seam-free one on the next scheduled rebuild.
        private bool _freshPending;
        // Zone damage is recomputed every tick but the blots it implies are
        // only materialised on this painter's scheduled turn.
        private bool _specklesDirty;
        // Set for the scheduled turn: the rectangle->projection swap replaces
        // pixels, so nothing may be carried over from the previous composite.
        private bool _forceFullRebuild;
        private int _speckleHash;
        // §40.8-H r11: то же самое для поля синяков (BluntDamage).
        private bool _bruisesDirty;
        private int _bruiseHash;

        public bool WantsFreshPass =>
            _materials != null && (_freshPending || HasPendingSeamUpgrade());

        // A decal confined to one submesh looks near enough the same drawn
        // either way, so it can ride to the scheduled turn. One that actually
        // STRADDLES a seam is visibly cut until it upgrades, and a sweep can be
        // ~19 s long — which reads as "the decal does not carry over onto the
        // next body part", i.e. the whole feature failing.
        private bool HasPendingSeamUpgrade()
        {
            foreach (var stamp in _stamps.Values)
            {
                if (stamp.DrawAsRect && stamp.NeedsSeamUpgrade && stamp.GeometryReady)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Draw just what appeared, on top of what is already there —
        /// no base blit, no earlier stamp redrawn. The exception is a decal
        /// that turned out to cross a seam: swapping its rectangle for the
        /// projection REPLACES pixels, so that one forces a full rebuild.</summary>
        public void PaintFresh()
        {
            _freshPending = false;
            foreach (var stamp in _stamps.Values)
            {
                if (stamp.DrawAsRect && stamp.NeedsSeamUpgrade && stamp.GeometryReady)
                {
                    stamp.DrawAsRect = false;
                    _forceFullRebuild = true;
                }
            }

            RepaintAll();
            _forceFullRebuild = false;
        }

        /// <summary>This painter's scheduled turn: rebuild everything, promote
        /// the tan into the base, reconcile the damage speckles, and upgrade
        /// every remaining rectangle to its seam-free form.</summary>
        public void PaintCycle()
        {
            _appliedTone = _skinTone;

            // The rectangle->projection swap REPLACES pixels, so the composite
            // is rebuilt from the base up. An additive pass would find the
            // stamp already listed as painted on its anchor slot, skip it, and
            // leave the clipped rectangle sitting there for good — the decal
            // would reach the neighbouring submesh but never stop being cut on
            // its own. That is exactly how leg->pelvis carry-over broke.
            _forceFullRebuild = true;
            foreach (var stamp in _stamps.Values)
            {
                stamp.DrawAsRect = false;
            }

            ReconcileSpeckles();
            ReconcileBruises();
            _freshPending = false;
            RepaintAll();
            _forceFullRebuild = false;
        }

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
            // Painting is driven by SkinPaintScheduler, not by this
            // component's own Update — see spec 40.8-K.
            enabled = false;
            SkinPaintScheduler.Register(this);
            _body = body;
            _bones = bones;
            _bodyRoot = bodyRoot;
            _npcId = npcId;
            _height = 1.7f * bodyRoot.lossyScale.y;
            _skinSlots = new HashSet<int>(skinSlots);
            _paintSlots = new int[_skinSlots.Count];
            _skinSlots.CopyTo(_paintSlots);
            System.Array.Sort(_paintSlots);
            // Spec 40.8-G: baked placement points (falls back to the legacy
            // BakeMesh path — with its combat-frame cost — when missing).
            if (!string.IsNullOrEmpty(actorMesh))
            {
                var vertexCount = body.sharedMesh != null ? body.sharedMesh.vertexCount : 0;
                _map = PaintPointMap.Load($"skin_{actorMesh}", vertexCount);
                // Spec 40.8-J: both halves must be present and current — the
                // frames live in the point map, the texels in the position
                // maps. Either one stale and decals stay per-slot (clipped at
                // UV seams) rather than landing in the wrong place.
                if (_map != null && _map.HasProjectedFrames)
                {
                    _posMaps = SkinPositionMapSet.Load($"skinpos_{actorMesh}", vertexCount);
                }
            }

            _materials = body.materials; // instantiate once, per NPC
            _originalAlbedo = new Texture?[_materials.Length];
            _originalNormal = new Texture?[_materials.Length];
            _slotRt = new RenderTexture?[_materials.Length];
            _slotRtNormal = new RenderTexture?[_materials.Length];
            _slotRtGloss = new RenderTexture?[_materials.Length];
            _glossLive = new bool[_materials.Length];
            _albedoLive = new bool[_materials.Length];
            _paintedSig = new int[_materials.Length];
            _paintedKeys = new HashSet<string>[_materials.Length];
            _paintedAlpha = new Dictionary<string, float>[_materials.Length];
            _paintedTone = new Color[_materials.Length];
            _paintedWet = new float[_materials.Length];
            for (var i = 0; i < _materials.Length; i++)
            {
                _paintedKeys[i] = new HashSet<string>();
                _paintedAlpha[i] = new Dictionary<string, float>();
            }

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

        /// <summary>True while a slot's albedo carries painted marks
        /// (wounds/bandages/droplets). NpcActorView pins the _BaseColor
        /// multiply white on these slots — the tan/sunburn/grime tone is
        /// baked into the texture instead (SetSkinTone), so the marks on top
        /// keep their true colour rather than being tinted by the tan.</summary>
        public bool SlotHasAlbedoPaint(int slot) =>
            slot >= 0 && slot < _albedoLive.Length && _albedoLive[slot];

        /// <summary>
        /// Tone currently baked into a live albedo target. It can lag the
        /// requested tone until this slot receives its scheduled repaint.
        /// </summary>
        public Color PaintedSkinTone(int slot) =>
            slot >= 0 && slot < _paintedTone.Length ? _paintedTone[slot] : _skinTone;

        /// <summary>Spec 40.7: the current tan/sunburn/grime skin tone. Baked
        /// into the paint target's BASE layer (UNDER the wound/bandage stamps)
        /// so a bandage or wound on a tanned body keeps its true colour instead
        /// of browning. A tone change re-bakes painted slots on the next Sync
        /// (it is folded into the repaint state hash); slots with no marks keep
        /// the cheap _BaseColor tint on the material.</summary>
        public void SetSkinTone(Color tone)
        {
            _skinTone = tone;
        }

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
        /// wounds: (zone, seed, heal01) records; bandaged: zones under a
        /// dressing; sweat01 + uncovered drive the painted water droplets (40.8
        /// v4); wetSmoothness is the caller's current wet-skin gloss — it
        /// becomes the gloss map's base so droplets sit ON the wet sheen.
        /// New wounds raycast-place once; heals repaint with lower alpha;
        /// fully healed marks vanish (composite rebuilt from the original).
        /// zoneDamage (spec 40.8-H r10): per-zone BloodSoil 0..1 (накопительная
        /// кровяная подложка из сима — растёт от ран, смывается водой; severed
        /// zones excluded upstream) driving the blood-speckle field.
        /// zoneBruise (spec 40.8-H r11): per-zone BluntDamage 0..1 — то же
        /// поле частиц фиолетовым и прозрачнее, сходит вместе с заживлением
        /// тупой травмы (мытьём НЕ смывается — это под кожей).
        /// </summary>
        public void Sync(List<(string zone, int seed, float heal)> wounds, HashSet<string> bandaged,
            float sweat01 = 0f, HashSet<string>? uncovered = null, float wetSmoothness = 0.32f,
            HashSet<string>? gauzed = null,
            List<(string zone, float damage01)>? zoneDamage = null,
            List<(string zone, float bruise01)>? zoneBruise = null,
            List<(string zone, int seed, float bleed)>? bleeds = null,
            List<(string zone, int seed)>? plasters = null)
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

            // The simulation keeps herbal and medkit dressings distinct for
            // supplies/effects; both deliberately render as the same white
            // gauze so a visible bandage never turns into a leaf ornament.
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

            // ⭐ §118.2: ПЛАСТЫРЬ — точечная наклейка на одну рану.
            //
            // Это ровно старый круглый бинт, который до §118.2 был единственным
            // видом перевязки: тот же штамп, то же место (сид раны → те же
            // броски t/azimuth, что и у самой раны), только мельче. Он никуда
            // не делся — он стал дешёвым расходником, а дорогой бинт поднялся
            // до обмотки всей зоны. Оба видны одновременно: у пластыря свои
            // ключи, и обмотка их не перекрывает, потому что рисуется раньше.
            _plasterPending.Clear();
            if (plasters != null && plasters.Count > 0)
            {
                foreach (var (zone, seed) in plasters)
                {
                    var key = $"p{seed}";
                    _desired.Add(key);
                    _alpha[key] = 1f;
                    stateHash = stateHash * 31 + key.GetHashCode();
                    _plasterPending[key] = (zone, seed);
                    if (!_stamps.ContainsKey(key))
                    {
                        needsPlacement = true;
                    }
                }
            }

            // ⭐ §118.2: кровь, проступающая СКВОЗЬ повязку.
            //
            // Пятно копится, а не читается из кадра: чем дольше рана течёт под
            // бинтом, тем оно шире. Свежая повязка ставит Clot01 = 1, поэтому
            // перевязанная заново рана просто перестаёт приходить в bleeds —
            // ключ выпадает из _desired, штамп сметается общей уборкой, и
            // «сменили бинт — пятно ушло» получается само.
            _bleedPending.Clear();
            if (bleeds != null && bleeds.Count > 0 && _map != null)
            {
                var dt = Mathf.Max(Time.deltaTime, 0f);
                foreach (var (zone, seed, bleed) in bleeds)
                {
                    _soak.TryGetValue(seed, out var soak);
                    soak = Mathf.Clamp01(soak + bleed * dt / BloodSoakSeconds);
                    _soak[seed] = soak;

                    // Ступень, а не сырое значение: размер входит в ключ, и
                    // непрерывный размер означал бы перекладку каждый кадр.
                    var step = Mathf.Clamp(
                        Mathf.FloorToInt(soak * BloodSoakSteps), 0, BloodSoakSteps - 1);
                    var key = $"bl{seed}#{step}";
                    _desired.Add(key);
                    _alpha[key] = 1f;
                    stateHash = stateHash * 31 + key.GetHashCode();
                    _bleedPending[key] = (zone, seed, step);
                    if (!_stamps.ContainsKey(key))
                    {
                        needsPlacement = true;
                    }
                }
            }

            // Рана перестала течь (свернулась, зажила, зону перевязали заново)
            // — забыть её историю, иначе следующее кровотечение той же раны
            // начнётся сразу с большого пятна.
            if (_soak.Count > 0)
            {
                _soakSweep.Clear();
                if (bleeds != null)
                {
                    foreach (var (_, seed, _) in bleeds)
                    {
                        _soakSweep.Add(seed);
                    }
                }

                _soakDrop.Clear();
                foreach (var seed in _soak.Keys)
                {
                    if (!_soakSweep.Contains(seed))
                    {
                        _soakDrop.Add(seed);
                    }
                }

                foreach (var seed in _soakDrop)
                {
                    _soak.Remove(seed);
                }
            }

            // Spec 40.8-H: zone-damage speckles. Gated on the baked map —
            // ~100 stamps through the legacy ClosestSkinTriangle fallback
            // would re-create the 40.8-G combat-frame cost, so a no-map actor
            // simply gets none. Count/alpha are pure functions of the 0.05-
            // quantized effective damage, so folding zone+bucket into the
            // hash covers both; healing shrinks the count and the stale sweep
            // below removes the tail keys for free. (A damaged zone with no
            // wound records now flips its slots to the painted RT — in
            // practice zone damage always coexists with wounds, so this
            // rarely creates RTs that would not exist anyway.)
            if (zoneDamage != null && zoneDamage.Count > 0 && _map != null)
            {
                // Spec 40.8-K: the speckle field is the SLOW half of the
                // picture — the gradual reddening of a battered limb, not the
                // bite you just took. It is also the BOTTOM layer, so adding
                // one blot forbids drawing additively (a new speckle would
                // land on top of the wounds instead of under them) and drags a
                // full rebuild with it. During a fight the zone-damage bucket
                // moves constantly, so letting speckles into the fresh lane
                // would mean a full rebuild on almost every bite — exactly the
                // cost this scheduling exists to avoid.
                //
                // So the damage is COMPUTED here every tick (cheap, no
                // allocation) and merely remembered; the stamps themselves are
                // reconciled on this painter's scheduled turn.
                ComputeEffectiveZoneDamage(zoneDamage);
                var speckleHash = 17;
                foreach (var pair in _speckleQ)
                {
                    speckleHash = speckleHash * 31 + pair.Key.GetHashCode();
                    speckleHash = speckleHash * 31 + Mathf.RoundToInt(pair.Value * 20f);
                }

                stateHash = stateHash * 31 + speckleHash;
                if (speckleHash != _speckleHash)
                {
                    _speckleHash = speckleHash;
                    _specklesDirty = true;
                }

                // Keep the blots that are already painted alive through the
                // general stale sweep below — ReconcileSpeckles owns their
                // lifetime now.
                foreach (var pair in _stamps)
                {
                    if (pair.Value.IsSpeckle)
                    {
                        _desired.Add(pair.Key);
                    }
                }
            }
            else
            {
                // Nothing damaged any more. Clear the demand and hand the
                // leftovers to the reconciler on the next turn — dropping them
                // here would leave _speckleHash describing a field that no
                // longer exists, and the same damage returning later would
                // then look "unchanged" and never re-place a single blot.
                if (_speckleQ.Count > 0 || _speckleHash != 0)
                {
                    _speckleQ.Clear();
                    _speckleHash = 0;
                    _specklesDirty = true;
                }

                foreach (var pair in _stamps)
                {
                    if (pair.Value.IsSpeckle)
                    {
                        _desired.Add(pair.Key);
                    }
                }
            }

            // §40.8-H r11: поле СИНЯКОВ — та же машинерия, свой источник
            // (BluntDamage зоны) и свой хэш. Без neighbour-bleed: синяк не
            // растекается на соседнюю зону, он ровно там, куда пришёлся удар.
            if (zoneBruise != null && zoneBruise.Count > 0 && _map != null)
            {
                ComputeZoneBruise(zoneBruise);
                var bruiseHash = 19;
                foreach (var pair in _bruiseQ)
                {
                    bruiseHash = bruiseHash * 31 + pair.Key.GetHashCode();
                    bruiseHash = bruiseHash * 31 + Mathf.RoundToInt(pair.Value * 20f);
                }

                stateHash = stateHash * 31 + bruiseHash;
                if (bruiseHash != _bruiseHash)
                {
                    _bruiseHash = bruiseHash;
                    _bruisesDirty = true;
                }
            }
            else if (_bruiseQ.Count > 0 || _bruiseHash != 0)
            {
                _bruiseQ.Clear();
                _bruiseHash = 0;
                _bruisesDirty = true;
            }

            foreach (var pair in _stamps)
            {
                if (pair.Value.IsBruise)
                {
                    _desired.Add(pair.Key);
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

            // A skin-tone change (tanning, sunburn fading, getting dirty) must
            // re-bake the BASE layer of every painted slot — the tan lives in
            // the texture now, under the stamps. Fold it into the hash so a
            // pure-tone change still triggers one repaint. Only painted slots
            // exist here (_desired non-empty), and it is quantized, so slow
            // drift crosses a bucket every few seconds, not every frame.
            if (_desired.Count > 0)
            {
                stateHash = stateHash * 31 + SkinToneHash(_skinTone);
            }

            // GPU-loss watchdog ("grey vinyl" bug): the painted skin lives in
            // RenderTextures whose contents the GPU can discard mid-session
            // (display sleep, fullscreen/resolution switch, device reset).
            // The materials keep sampling the dead RT, so every painted slot
            // renders as a uniform glossy-grey body until the next heal-bucket
            // repaint — minutes away. IsCreated() flips false on loss, and
            // binding the RT during a repaint re-creates it, so forcing a
            // repaint here fully restores the skin the same frame.
            var targetsLost = AnyPaintRtLost();
            if (stateHash == _lastStateHash && !needsPlacement && !targetsLost)
            {
                return; // nothing changed — no repaint
            }

            _lastStateHash = stateHash;

            if (needsPlacement)
            {
                PlaceNewStamps(wounds, bandaged, sweat, uncovered, gauzed);
            }

            // A brand-new mark (or a lost target) must not wait for this
            // painter's turn in the cycle — the fresh lane draws it next frame.
            if (needsPlacement || targetsLost)
            {
                _freshPending = true;
            }
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

            // §118.2: пластыри — по одному на заклеенную рану.
            foreach (var pending in _plasterPending)
            {
                if (!_stamps.ContainsKey(pending.Key))
                {
                    TryPlace(pending.Key, pending.Value.zone, pending.Value.seed,
                        isBandage: true, isGauze: false, isPlaster: true);
                }
            }

            // §118.2: пятна крови поверх повязок.
            foreach (var pending in _bleedPending)
            {
                if (!_stamps.ContainsKey(pending.Key))
                {
                    TryPlaceBleed(pending.Key, pending.Value.zone,
                        pending.Value.seed, pending.Value.step);
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

            // Speckles are NOT placed here — they belong to the scheduled
            // turn (ReconcileSpeckles), see the note in Sync.
        }

        // Brings the blot stamps in line with the damage Sync last computed:
        // adds what appeared, drops what healed away, refreshes the alphas.
        // Runs on the scheduled turn only, so a fight cannot make it churn.
        private readonly HashSet<string> _speckleDesired = new();

        private void ReconcileSpeckles()
        {
            if (!_specklesDirty || _map == null)
            {
                return;
            }

            _specklesDirty = false;
            _speckleDesired.Clear();

            foreach (var pair in _speckleQ)
            {
                var count = SpeckleCountFor(pair.Key, pair.Value);
                if (count <= 0)
                {
                    continue;
                }

                var alphaBase = Mathf.Lerp(SpeckleAlphaMin, SpeckleAlphaMax,
                    SpeckleRamp(pair.Value));
                for (var i = 0; i < count; i++)
                {
                    var key = SpeckleKey(pair.Key, i);
                    _speckleDesired.Add(key);
                    // Stable per-blot variance so the field isn't uniform.
                    _alpha[key] = alphaBase * (0.8f + 0.2f * SpeckleJitter01(pair.Key, i));
                    if (!_stamps.ContainsKey(key))
                    {
                        TryPlaceSpeckle(key, pair.Key, i);
                    }
                }
            }

            _stale.Clear();
            foreach (var pair in _stamps)
            {
                if (pair.Value.IsSpeckle && !_speckleDesired.Contains(pair.Key))
                {
                    _stale.Add(pair.Key);
                }
            }

            foreach (var key in _stale)
            {
                _stamps.Remove(key);
                _alpha.Remove(key);
            }
        }

        // §40.8-H r11: то же для синяков. Отдельный набор ключей (`bz{zone}#i`),
        // отдельная сетка обхода — иначе синяк сел бы ровно под каждым
        // кровяным пятном и они читались бы как одно грязное месиво.
        private readonly HashSet<string> _bruiseDesired = new();

        private void ReconcileBruises()
        {
            if (!_bruisesDirty || _map == null)
            {
                return;
            }

            _bruisesDirty = false;
            _bruiseDesired.Clear();

            foreach (var pair in _bruiseQ)
            {
                var count = BruiseCountFor(pair.Key, pair.Value);
                if (count <= 0)
                {
                    continue;
                }

                var alphaBase = Mathf.Lerp(BruiseAlphaMin, BruiseAlphaMax,
                    SpeckleRamp(pair.Value));
                for (var i = 0; i < count; i++)
                {
                    var key = BruiseKey(pair.Key, i);
                    _bruiseDesired.Add(key);
                    _alpha[key] = alphaBase * (0.75f + 0.25f * SpeckleJitter01(pair.Key, i + 613));
                    if (!_stamps.ContainsKey(key))
                    {
                        TryPlaceBruise(key, pair.Key, i);
                    }
                }
            }

            _stale.Clear();
            foreach (var pair in _stamps)
            {
                if (pair.Value.IsBruise && !_bruiseDesired.Contains(pair.Key))
                {
                    _stale.Add(pair.Key);
                }
            }

            foreach (var key in _stale)
            {
                _stamps.Remove(key);
                _alpha.Remove(key);
            }
        }

        // ---- Spec 40.8-H zone-damage speckle helpers ----

        // zone -> own damage / quantized onset-remapped effective damage.
        private readonly Dictionary<string, float> _speckleOwn = new();
        private readonly Dictionary<string, float> _speckleQ = new();
        // r11: то же для синяков (без neighbour-bleed — только own).
        private readonly Dictionary<string, float> _bruiseQ = new();

        private static readonly Dictionary<string, List<string>> BruiseKeyCache = new();

        private static string BruiseKey(string zone, int index)
        {
            if (!BruiseKeyCache.TryGetValue(zone, out var list))
            {
                list = new List<string>();
                BruiseKeyCache[zone] = list;
            }

            while (list.Count <= index)
            {
                list.Add($"bz{zone}#{list.Count}");
            }

            return list[index];
        }

        // Синяк локален: никакого растекания на соседей, только собственный
        // BluntDamage зоны через тот же onset и те же 0.05-бакеты.
        private void ComputeZoneBruise(List<(string zone, float bruise01)> zoneBruise)
        {
            _bruiseQ.Clear();
            foreach (var (zone, raw) in zoneBruise)
            {
                var q = Mathf.Clamp01((Mathf.Clamp01(raw) - SpeckleOnsetDamage) /
                    (1f - SpeckleOnsetDamage));
                if (q <= 0f)
                {
                    continue;
                }

                q = Mathf.Round(q * 20f) / 20f;
                if (q > 0f)
                {
                    _bruiseQ[zone] = q;
                }
            }
        }

        // Speckle keys are hot (up to ~100 per NPC per Sync) — cache the
        // strings once, shared across all NPCs.
        private static readonly Dictionary<string, List<string>> SpeckleKeyCache = new();

        private static string SpeckleKey(string zone, int index)
        {
            if (!SpeckleKeyCache.TryGetValue(zone, out var list))
            {
                list = new List<string>();
                SpeckleKeyCache[zone] = list;
            }

            while (list.Count <= index)
            {
                list.Add($"dz{zone}#{list.Count}");
            }

            return list[index];
        }

        private static float SpeckleJitter01(string zone, int index)
        {
            var state = (uint)(zone.GetHashCode() * 31 + index * 977) | 1u;
            return NextRand(ref state);
        }

        // eff[z] = max(own, NeighborBleed * max(adjacent own)), then remapped
        // through the onset so hp above ~0.85 stays clean. Only zones present
        // in the input participate — severed zones are excluded upstream, so
        // they neither draw speckles nor donate bleed (the stump wound is the
        // visual there).
        private void ComputeEffectiveZoneDamage(List<(string zone, float damage01)> zoneDamage)
        {
            _speckleOwn.Clear();
            _speckleQ.Clear();
            foreach (var (zone, dmg) in zoneDamage)
            {
                _speckleOwn[zone] = Mathf.Clamp01(dmg);
            }

            foreach (var pair in _speckleOwn)
            {
                var eff = pair.Value;
                if (SpeckleAdjacency.TryGetValue(pair.Key, out var neighbours))
                {
                    foreach (var neighbour in neighbours)
                    {
                        if (_speckleOwn.TryGetValue(neighbour, out var nd))
                        {
                            eff = Mathf.Max(eff, nd * SpeckleNeighborBleed);
                        }
                    }
                }

                var q = Mathf.Clamp01((eff - SpeckleOnsetDamage) / (1f - SpeckleOnsetDamage));
                if (q <= 0f)
                {
                    continue;
                }

                q = Mathf.Round(q * 20f) / 20f; // 0.05 buckets — repaint per step
                if (q > 0f)
                {
                    _speckleQ[pair.Key] = q;
                }
            }
        }

        // Placement is a straight grid-cell walk, NOT PointAt: PointAt
        // quantizes (t, azimuth) onto the 8×16 grid, so two dozen "random"
        // rolls collide into duplicate cells. A coprime stride visits every
        // cell exactly once per cycle — the fill grows evenly with damage,
        // and existing speckles never move when the count grows. No probing
        // on an invalid cell (that would shift cells later indices own).
        private void TryPlaceSpeckle(string key, string zoneName, int index)
        {
            var points = _map!.PointsFor(zoneName);
            var n = points.Length;
            if (n == 0)
            {
                PlaceTombstone(key, index, isBandage: false);
                return;
            }

            var start = (int)((((uint)(_npcId * 40503)) ^ (uint)zoneName.GetHashCode()) % (uint)n);
            var point = points[(start + index * SpeckleCellStride) % n];
            if (!point.Valid)
            {
                PlaceTombstone(key, index, isBandage: false);
                return;
            }

            EnsureStampTextures();
            var state = (uint)(_npcId * 83492791 ^ (zoneName.GetHashCode() * 31 + index)) | 1u;
            var targetWorld = (SpeckleWorldSizeMin +
                               NextRand(ref state) * (SpeckleWorldSizeMax - SpeckleWorldSizeMin)) *
                              (_height / 1.7f);
            SizeFromDensity(point, targetWorld, out var sizeU, out var sizeV, minUv: 0.004f);
            sizeU = Mathf.Min(sizeU, SpeckleMaxUvSize);
            sizeV = Mathf.Min(sizeV, SpeckleMaxUvSize);

            _stamps[key] = new Stamp
            {
                Key = key,
                Slot = point.Slot,
                Uv = point.Uv,
                Seed = index,
                UvSizeX = sizeU,
                UvSizeY = sizeV,
                Over = SpeckleVariant(ref state),
                IsSpeckle = true,
                RotationDeg = NextRand(ref state) * 360f
            };
        }

        // §40.8-H r11: синяк размещается той же прогулкой по сетке, что и
        // спекл, но со СВОИМ стартом и своим шагом — иначе синяк №i сел бы
        // ровно в ячейку кровяного пятна №i и оба читались бы как одно
        // грязное месиво. Крупнее, реже, прозрачнее.
        private void TryPlaceBruise(string key, string zoneName, int index)
        {
            var points = _map!.PointsFor(zoneName);
            var n = points.Length;
            if (n == 0)
            {
                PlaceTombstone(key, index, isBandage: false);
                return;
            }

            EnsureStampTextures();
            var start = (int)((((uint)(_npcId * 40503 + 7919)) ^
                (uint)(zoneName.GetHashCode() * 17)) % (uint)n);
            var state = (uint)(_npcId * 22468223 ^ (zoneName.GetHashCode() * 61 + index)) | 1u;
            var targetWorld = (BruiseWorldSizeMin +
                               NextRand(ref state) * (BruiseWorldSizeMax - BruiseWorldSizeMin)) *
                              (_height / 1.7f);

            // Plain walk, like the blood blots — see the note on
            // SpeckleCellStride for why edge-avoidance was reverted.
            var point = points[(start + index * BruiseCellStride) % n];
            if (!point.Valid)
            {
                PlaceTombstone(key, index, isBandage: false);
                return;
            }

            SizeFromDensity(point, targetWorld, out var sizeU, out var sizeV, minUv: 0.004f);
            sizeU = Mathf.Min(sizeU, SpeckleMaxUvSize);
            sizeV = Mathf.Min(sizeV, SpeckleMaxUvSize);

            _stamps[key] = new Stamp
            {
                Key = key,
                Slot = point.Slot,
                Uv = point.Uv,
                Seed = index,
                UvSizeX = sizeU,
                UvSizeY = sizeV,
                Over = _texBruise,
                IsBruise = true,
                RotationDeg = NextRand(ref state) * 360f
            };
        }

        // r3: every speckle is the SAME art — blood_stain, the red splatter
        // the molly damage decal sprayed on hit (the user asked for exactly
        // this picture; the r1 splash/splat/scratch mix read wrong). The
        // seeded roll only varies nothing today but keeps the signature so
        // variants can return without touching callers. Fallback chain
        // covers an un-imported PNG — fewer shapes, never a hole.
        private static Texture? SpeckleVariant(ref uint state)
        {
            _ = NextRand(ref state);
            if (_texStain != null)
            {
                return _texStain;
            }

            return _texSplash != null ? _texSplash : _texSplat;
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

        /// <summary>§118.2: клякса, проступившая сквозь повязку. Садится РОВНО
        /// на рану — берёт тот же сид и те же броски (t, azimuth), что и сама
        /// рана в TryPlace, поэтому кровь выступает там, где порез, а не в
        /// случайной точке зоны. Растёт ступенями по мере промокания.</summary>
        private void TryPlaceBleed(string key, string zoneName, int seed, int step)
        {
            if (_map == null || !Zones.ContainsKey(zoneName))
            {
                PlaceTombstone(key, seed, isBandage: false);
                return;
            }

            var mapState = (uint)(_npcId * 73856093 ^ seed) | 1u;
            var mapT = 0.25f + NextRand(ref mapState) * 0.5f;
            var mapAzimuth = NextRand(ref mapState) * Mathf.PI * 2f;
            var points = _map.PointsFor(zoneName);
            var point = _map.PointAt(points, mapT, mapAzimuth);
            if (points.Length == 0 || !point.Valid)
            {
                PlaceTombstone(key, seed, isBandage: false);
                return;
            }

            EnsureStampTextures();

            // Точка -> клякса. Нижний край мельче раны (первая капля читается
            // как точка), верхний заметно её шире, но не во всю обмотку.
            var grow = (step + 1) / (float)BloodSoakSteps;
            var target = Mathf.Lerp(0.020f, 0.085f, grow) * (_height / 1.7f);
            SizeFromDensity(point, target, out var sizeU, out var sizeV);

            _stamps[key] = new Stamp
            {
                Key = key,
                Slot = point.Slot,
                Uv = point.Uv,
                Seed = seed,
                UvSizeX = sizeU,
                UvSizeY = sizeV,
                Under = null,
                Over = _texBleed,
                OverGloss = null,
                OverNormal = null,
                IsBleed = true
            };
        }

        private void TryPlace(string key, string zoneName, int seed, bool isBandage,
            bool isGauze = false, bool isPlaster = false)
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

                // ⭐ §118.2: повязка — не круглая нашлёпка, а ФИКСИРОВАННАЯ
                // обмотка зоны, одинаковая у всех.
                //
                // Раньше бинт был обычным штампом: рулетка (t, azimuth) катала
                // ему место на конечности и клала круг размером 0.14×рост. Это
                // и читалось как пластырь, а не как бинт, — и никогда не могло
                // обмотать конечность кругом, потому что штамп есть пятно.
                //
                // Оверлей зоны уже нарисован В UV ЭТОГО ТАЙЛА: полоса стоит на
                // своём месте, всё остальное прозрачно. Поэтому позиция не
                // катается вовсе — штамп кладётся на ВЕСЬ тайл (центр 0.5/0.5,
                // размер 1×1, без спина), а где именно лечь, решает альфа
                // картинки. Отсюда и «всегда бинтуем одинаково».
                //
                // Развёртка у всех актёров общая (Genesis 3, UDIM: Torso —
                // тайл 1, Legs — 2, Arms — 3), так что один оверлей на зону
                // годится любой колонистке.
                // Пластырь идёт СТАРЫМ путём — точечная наклейка на ране, — и
                // потому обмотку зоны обходит стороной.
                if (isBandage && !isPlaster)
                {
                    var wrap = WrapOverlayFor(zoneName);
                    // Оверлей ещё ЕДЕТ (запись в реестре есть, текстура нет) —
                    // не приколачивать старую круглую нашлёпку: созданный ключ
                    // никогда не пересоздаётся, и бинт до конца сессии выглядел
                    // бы пластырем. Пропуск хода безопасен: пока ключа нет в
                    // _stamps, Sync каждый тик поднимает needsPlacement и
                    // размещение повторяется — обмотка ляжет, как только
                    // текстура доедет. Если записи в реестре нет вовсе, честно
                    // падаем на старый штамп ниже.
                    if (wrap == null && WrapRects.ContainsKey(zoneName) &&
                        HexLive.UnityPresentation.Content.ContentAssetService.Instance
                            .TryResolveLegacyPath(
                                $"HexLive/Decals/bandage_wrap_{zoneName}", out _))
                    {
                        return;
                    }

                    if (wrap != null && WrapRects.TryGetValue(zoneName, out var wrapRect))
                    {
                        _stamps[key] = new Stamp
                        {
                            Key = key,
                            Slot = point.Slot,
                            Uv = new Vector2(wrapRect.x, wrapRect.y),
                            Seed = seed,
                            UvSizeX = wrapRect.width,
                            UvSizeY = wrapRect.height,
                            Under = null,
                            Over = wrap,
                            OverGloss = null,
                            OverNormal = WrapNormalFor(zoneName),
                            IsBandage = true,
                            IsGauze = isGauze
                        };
                        return;
                    }
                }

                // §118.2: наклейка чуть мельче прежнего круглого бинта (0.14 →
                // 0.10) — она закрывает ОДИН порез, а не «область раны».
                var mapTarget = (isPlaster ? 0.10f
                                    : isBandage ? 0.14f
                                    : 0.07f + NextRand(ref mapState) * 0.04f)
                                * (_height / 1.7f);

                // Spec 40.8-J: the seam-free path. Same seeded rolls, same
                // grid cell, same art — only the FOOTPRINT changes, from a
                // rectangle in one slot's UV to a box in body space.
                if (TryPlaceProjected(key, zoneName, seed, isBandage, isGauze, point, mapTarget))
                {
                    return;
                }

                SizeFromDensity(point, mapTarget, out var mapSizeU, out var mapSizeV);
                var (mover, mgloss, mnormal) = WoundVariant(seed);
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
                    // A little wet-core relief so the wound catches light (see
                    // WoundReliefStrength); bandages/gauze stay smooth.
                    OverNormal = isBandage ? null : mnormal,
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
            var (wover, wgloss, wnormal) = WoundVariant(seed);
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
                OverGloss = isBandage ? null : wgloss,
                // A LITTLE wet-core relief so the wound actually catches light.
                // v5 cut wound relief because detailed normals flared as lit
                // ridges at UV seams — re-added at WoundReliefStrength (0.35),
                // soft enough to keep seam flares faint. Bandages stay smooth;
                // droplets keep their own full dome relief.
                OverNormal = isBandage ? null : wnormal,
                IsBandage = isBandage,
                IsGauze = isGauze
            };
        }

        /// <summary>
        /// Spec 40.8-J: records `key` as a PROJECTED stamp — a decal box in
        /// bind (mesh) space rather than a rectangle in one slot's UV. Returns
        /// false when the actor has no baked position maps (or the point sits
        /// on a slot with no paintable texture), in which case the caller
        /// keeps the legacy per-slot rect.
        ///
        /// The frame is built from the baked surface point: Z = the surface
        /// normal, Y = the zone's bone axis flattened onto the surface (so the
        /// art runs ALONG the limb, as the UV-aligned rect used to), X = their
        /// cross product. Sizes come in world metres and convert to mesh units
        /// through the map's own bind-pose height, so a body exported at a
        /// different scale still gets a 14 cm wrap.
        /// </summary>
        private bool TryPlaceProjected(string key, string zoneName, int seed, bool isBandage,
            bool isGauze, in PaintPointMap.Point point, float targetWorld)
        {
            if (_posMaps == null || _map == null)
            {
                return false;
            }

            if (point.BindNormal.sqrMagnitude < 1e-8f || _posMaps.GroupOf(point.Slot) < 0)
            {
                return false; // pre-v2 cell, or a slot with nothing to paint into
            }

            var zone = _map.ZoneFor(zoneName);
            var axis = zone != null ? zone.BindAxisDir : Vector3.up;

            // World metres -> mesh units. _height already carries the actor's
            // scale, and the mesh is authored at MeshHeight.
            var meshScale = Mathf.Max(0.0001f, _map.MeshHeight / 1.7f);
            var size = targetWorld / Mathf.Max(0.0001f, _height / 1.7f) * meshScale;

            var normal = point.BindNormal.normalized;
            var along = axis - normal * Vector3.Dot(normal, axis);
            if (along.sqrMagnitude < 1e-6f)
            {
                // Bone axis parallel to the normal (the head's stub axis on a
                // crown texel): any tangent will do.
                along = Vector3.Cross(normal, Vector3.right);
                if (along.sqrMagnitude < 1e-6f)
                {
                    along = Vector3.Cross(normal, Vector3.forward);
                }
            }

            along.Normalize();
            // LookRotation(forward=normal, up=along): local +Z is the surface
            // normal, +Y runs along the limb, +X around it.
            var frame = Matrix4x4.TRS(point.BindPos,
                Quaternion.LookRotation(normal, along), Vector3.one).inverse;
            var scale = Matrix4x4.Scale(new Vector3(1f / size, 1f / size, 1f));
            var underScale = Matrix4x4.Scale(new Vector3(1f / (size * 1.6f), 1f / (size * 1.6f), 1f));

            var depth = size * ProjectedDepthFactor;
            // Claim-test box = the decal's own box, grown by the sample
            // spacing (xy is normalized by `size`, z is in mesh units).
            var slack = _posMaps.SampleSpacing + ProjectedReachMargin;
            var halfExtents = new Vector3(0.5f + slack / size, 0.5f + slack / size, depth + slack);

            EnsureStampTextures();
            var (over, gloss, _) = WoundVariant(seed);
            // Spec 40.8-K: the plain rectangle is filled in too. It costs two
            // divisions and it is what the fresh lane paints the instant the
            // bite lands or the dressing goes on — the seam-free projection
            // needs a worker pass and a scheduled rebuild, and a wound the
            // player cannot see for ten seconds is not a wound.
            SizeFromDensity(point, targetWorld, out var rectSizeU, out var rectSizeV);
            var stamp = new Stamp
            {
                Key = key,
                Slot = point.Slot,
                Uv = point.Uv,
                UvSizeX = rectSizeU,
                UvSizeY = rectSizeV,
                DrawAsRect = true,
                Seed = seed,
                Under = isBandage ? null : _texSplash,
                Over = isGauze ? _texGauze : (isBandage ? _texBandage : over),
                OverGloss = isBandage ? null : gloss,
                // No relief on the projected path: the wound's normal art is
                // authored in the DECAL's tangent frame, which is not the
                // surface's UV frame, so stamping it would tilt the lighting.
                // The wet gloss carries the volume instead (the same call
                // spec 40.8-D v5 made when relief flared at seams).
                OverNormal = null,
                IsBandage = isBandage,
                IsGauze = isGauze,
                IsProjected = true,
                ObjectToDecal = scale * frame,
                UnderToDecal = underScale * frame,
                BindPos = point.BindPos,
                DecalNormal = normal,
                Depth = depth
            };

            _stamps[key] = stamp;
            var underSize = size * 1.6f;
            var underHalfExtents = new Vector3(
                0.5f + slack / underSize, 0.5f + slack / underSize,
                depth * 1.6f + slack);
            QueueGeometry(stamp, point.Slot, scale * frame, halfExtents,
                underHalfExtents, hasUnderlay: !isBandage);
            return true;
        }

        // ---- placement geometry, off the main thread ----
        //
        // Which slots a decal reaches (a scan of ~1200 baked surface samples
        // per slot) and the UV window on each (a 1024-cell grid scan) is the
        // expensive half of placing a stamp — and it lands exactly on the
        // frame a bite connects, which is what made a dog fight stutter. It is
        // pure struct math over baked, read-only arrays, so it runs on the
        // thread pool; the stamp simply paints nothing until it is ready
        // (at most one repaint interval later — the repaint is coalesced to
        // 0.25 s anyway, so nothing is visibly late).
        //
        // NOTHING in here may touch a Unity object: no `== null` on a
        // UnityEngine.Object (that reads native state), no Resources, no
        // textures. Vector3/Matrix4x4/Rect are plain structs and are safe.
        private int _pendingPlacements;
        private volatile bool _geometryArrived;

        private void QueueGeometry(Stamp stamp, int anchorSlot, Matrix4x4 objectToDecal,
            Vector3 halfExtents, Vector3 underHalfExtents, bool hasUnderlay)
        {
            var maps = _posMaps!;
            // Immutable after Construct; safe to share with read-only workers
            // and avoids one array allocation for every wound.
            var slots = _paintSlots;

            System.Threading.Interlocked.Increment(ref _pendingPlacements);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    ResolveGeometry(stamp, maps, slots, anchorSlot, objectToDecal,
                        halfExtents, underHalfExtents, hasUnderlay);
                }
                finally
                {
                    // Order matters: RAISE THE FLAG FIRST. LateUpdate turns the
                    // component off once nothing is pending, so decrementing
                    // first leaves a window where it sees "0 pending, nothing
                    // arrived", disables itself, and only then the flag is set
                    // — with no LateUpdate left to read it. The stamp would
                    // then wait for the next unrelated state change to appear.
                    _geometryArrived = true;
                    System.Threading.Interlocked.Decrement(ref _pendingPlacements);
                }
            });
        }

        private static void ResolveGeometry(Stamp stamp, SkinPositionMapSet maps, int[] skinSlots,
            int anchorSlot, Matrix4x4 objectToDecal, Vector3 halfExtents,
            Vector3 underHalfExtents, bool hasUnderlay)
        {
            var reached = new List<int>(skinSlots.Length) { anchorSlot };
            foreach (var slot in skinSlots)
            {
                if (slot != anchorSlot && maps.GroupOf(slot) >= 0 &&
                    maps.SlotReaches(slot, objectToDecal, halfExtents))
                {
                    reached.Add(slot);
                }
            }

            var windows = new Rect[reached.Count];
            var underWindows = new Rect[reached.Count];
            // Slots sharing the same source texture also share the same baked
            // position map and window. Resolve each group once; Genesis actors
            // commonly have three material slots in one group. The reached set
            // is tiny (normally 1-3), so a backwards lookup is cheaper than
            // allocating three group-sized scratch arrays per wound.
            for (var i = 0; i < reached.Count; i++)
            {
                var group = maps.GroupOf(reached[i]);
                if (group < 0)
                {
                    continue;
                }

                var reused = false;
                for (var previous = 0; previous < i; previous++)
                {
                    if (maps.GroupOf(reached[previous]) != group)
                    {
                        continue;
                    }

                    windows[i] = windows[previous];
                    underWindows[i] = underWindows[previous];
                    reused = true;
                    break;
                }

                if (reused)
                {
                    continue;
                }

                maps.TryGetUvBounds(group, objectToDecal, halfExtents, out windows[i]);
                if (hasUnderlay)
                {
                    maps.TryGetUvBounds(group, stamp.UnderToDecal, underHalfExtents,
                        out underWindows[i]);
                }
                else
                {
                    underWindows[i] = windows[i];
                }
            }

            stamp.NeedsSeamUpgrade = reached.Count > 1;
            stamp.Slots = reached.ToArray();
            stamp.Windows = windows;
            stamp.UnderWindows = underWindows;
            stamp.GeometryReady = true; // volatile: publishes the three above
        }

        private static bool StampTouchesSlot(Stamp stamp, int slot)
        {
            // A projected stamp has no trustworthy touched-slot set until the
            // worker publishes it. Treating the anchor as touched here used to
            // allocate an empty 2048² RT and record the stamp as already drawn;
            // the subsequent additive pass could then skip the real draw.
            // While the fresh lane owns it, the stamp is a plain rectangle on
            // its anchor slot — that is where it was drawn and where it must be
            // accounted for.
            if (stamp.IsProjected && stamp.DrawAsRect)
            {
                return stamp.Slot == slot;
            }

            if (stamp.IsProjected && !stamp.GeometryReady)
            {
                return false;
            }

            if (stamp.Slots == null)
            {
                return stamp.Slot == slot;
            }

            for (var i = 0; i < stamp.Slots.Length; i++)
            {
                if (stamp.Slots[i] == slot)
                {
                    return true;
                }
            }

            return false;
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

            if (stamp.IsBleed)
            {
                stamp.Over ??= _texBleed;
                return;
            }

            if (stamp.IsBandage)
            {
                // §118.2: восстановить ИМЕННО обмотку этой зоны. Ключ несёт её
                // имя ("b{zone}" / "g{zone}") — без разбора ключа домен-релоуд
                // подсунул бы сюда круглую нашлёпку и бинт молча превратился бы
                // в пластырь до следующего входа в игру.
                if (stamp.Key.Length > 1)
                {
                    var wrapZone = stamp.Key.Substring(1);
                    if (stamp.Over == null) stamp.Over = WrapOverlayFor(wrapZone);
                    if (stamp.OverNormal == null) stamp.OverNormal = WrapNormalFor(wrapZone);
                }

                stamp.Over ??= stamp.IsGauze ? _texGauze : _texBandage;
                return;
            }

            // Spec 40.8-H: speckles are albedo-only by design — backfilling
            // them below would grow a 1.6× blood-splash underlay plus wound
            // gloss/relief after a domain reload.
            if (stamp.IsSpeckle)
            {
                stamp.Over ??= _texStain != null ? _texStain : _texSplash;
                return;
            }

            // r11: то же для синяков — фиолетовая перекраска пересобирается
            // в EnsureStampTextures, здесь только восстановить ссылку.
            if (stamp.IsBruise)
            {
                stamp.Over ??= _texBruise;
                return;
            }

            stamp.Under ??= _texSplash;
            var (wover, wgloss, wnormal) = WoundVariant(stamp.Seed);
            stamp.Over ??= wover;
            stamp.OverGloss ??= wgloss;
            // Projected wounds carry no relief by design (see
            // TryPlaceProjected) — backfilling it here would put it back.
            if (!stamp.IsProjected)
            {
                stamp.OverNormal ??= wnormal;
            }
        }

        // Deterministic per-seed wound art: the same seed always resolves to
        // the same shape, so a save-replay and a late RefreshStampArt agree.
        // Skips variants whose PNG hasn't imported yet (partial import paints
        // fewer shapes, never crashes); index-aligned over+gloss+normal stay
        // paired. Variants with no authored _n borrow the generic blood-bead
        // normal so they still catch a little light.
        private static (Texture? over, Texture? gloss, Texture? normal) WoundVariant(int seed)
        {
            var n = _woundOver.Length;
            if (n == 0)
            {
                return (_texScratch, _texScratchG, _texScratchN);
            }

            var start = (int)((uint)seed % (uint)n);
            for (var k = 0; k < n; k++)
            {
                var i = (start + k) % n;
                if (_woundOver[i] != null)
                {
                    return (_woundOver[i], _woundGloss[i], _woundNormal[i] ?? _texSplatN);
                }
            }

            return (_texScratch, _texScratchG, _texScratchN);
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

        // §40.8-H r11: фиолетовая версия кровяного сплаттера для синяков.
        // Перекраска на лету, а не отдельный PNG: форма синяка обязана
        // совпадать с формой пятна, а исходник может быть без Read/Write —
        // поэтому копия через RT + ReadPixels, затем канал R (арт
        // красно-доминантный) становится маской багрово-фиолетового.
        private static Texture2D? MakeBruiseTexture(Texture2D? source)
        {
            if (source == null)
            {
                return null;
            }

            // sRGB на обоих концах явно: проект линейный, а арт — sRGB, и
            // Default-таргет прогнал бы копию через лишнюю гамма-конверсию
            // (фиолетовый уехал бы в грязно-синий).
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, rt);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = source.anisoLevel,
                name = "bruise_stain_runtime"
            };
            tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            var pixels = tex.GetPixels32();
            for (var i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                var intensity = System.Math.Max(c.r, System.Math.Max(c.g, c.b));
                pixels[i] = new Color32(
                    (byte)(intensity * 0.42f),
                    (byte)(intensity * 0.20f),
                    (byte)(intensity * 0.52f),
                    c.a);
            }

            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
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
            _texSplash = _texScratch = _texSplat = _texBandage = _texGauze = _texStain = null;
            _texSplashN = _texScratchN = _texSplatN = _texSweatN = null;
            _texScratchG = _texSplatG = null;
            _woundOver = System.Array.Empty<Texture2D?>();
            _woundGloss = System.Array.Empty<Texture2D?>();
            _woundNormal = System.Array.Empty<Texture2D?>();
            _normalDecode = null;
            _dropletStamp = null;
            _glossStamp = null;
            _skinTintBlit = null;
            _projectedStamp = null;
            _dropletShaderWarned = false;
            _projectedShaderWarned = false;
        }

        private static void EnsureStampTextures()
        {
            // Re-check while anything is missing (asset may import mid-session).
            if (_stampTexturesLoaded && _texScratchN != null && _texSplatN != null &&
                _texSplashN != null && _texSweatN != null && _dropletStamp != null &&
                _texScratchG != null && _texSplatG != null && _glossStamp != null &&
                _skinTintBlit != null && _texStain != null && _projectedStamp != null &&
                _texBruise != null)
            {
                return;
            }

            _stampTexturesLoaded = true;
            _texSplash = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/blood_splash");
            _texStain = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/blood_stain");
            _texScratch = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/wound_scratch");
            _texSplat = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/blood_splat");
            // Every dressing uses the same white gauze artwork. The simulation
            // still records whether the consumed item was herbal or medkit, but
            // that provenance must not swap a wound's visible material.
            _texGauze = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/gauze_wrap") ?? MakeGauzeTexture();
            _texBandage = _texGauze;
            _wrapOverlays.Clear();
            _wrapNormals.Clear();
            _texBleed = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/bandage_bleed");
            _texSplashN = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/blood_splash_n");
            _texScratchN = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/wound_scratch_n");
            _texSplatN = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/blood_splat_n");
            _texSweatN = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/sweat_drops_n");
            _texScratchG = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/wound_scratch_g");
            _texSplatG = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/blood_splat_g");
            // r11: фиолетовая версия сплаттера для синяков — перекраска того
            // же арта, поэтому строго ПОСЛЕ его загрузки. Раз за сессию.
            _texBruise ??= MakeBruiseTexture(_texStain ?? _texSplat ?? _texSplash);

            // Load the wound over-art variant table (over + matching gloss).
            // A missing variant PNG leaves nulls — WoundVariant() skips them,
            // so a partial import just paints fewer shapes, never crashes.
            _woundOver = new Texture2D?[WoundVariantNames.Length];
            _woundGloss = new Texture2D?[WoundVariantNames.Length];
            _woundNormal = new Texture2D?[WoundVariantNames.Length];
            for (var i = 0; i < WoundVariantNames.Length; i++)
            {
                _woundOver[i] = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>($"HexLive/Decals/{WoundVariantNames[i]}");
                _woundGloss[i] = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>($"HexLive/Decals/{WoundVariantNames[i]}_g");
                // Most variants have no _n — WoundVariant falls back to the
                // shared blood-bead normal for those.
                _woundNormal[i] = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>($"HexLive/Decals/{WoundVariantNames[i]}_n");
            }

            var decodeShader = Shader.Find("Hidden/HexLive/NormalDecodeBlit");
            _normalDecode = decodeShader != null ? new Material(decodeShader) : null;
            var dropletShader = Shader.Find("Hidden/HexLive/DropletStamp");
            _dropletStamp = dropletShader != null ? new Material(dropletShader) : null;
            var glossShader = Shader.Find("Hidden/HexLive/WoundGlossStamp");
            _glossStamp = glossShader != null ? new Material(glossShader) : null;
            var tintShader = Shader.Find("Hidden/HexLive/SkinTintBlit");
            _skinTintBlit = tintShader != null ? new Material(tintShader) : null;
            var projectedShader = Shader.Find("Hidden/HexLive/ProjectedStamp");
            _projectedStamp = projectedShader != null ? new Material(projectedShader) : null;
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

        // Spec 40.8-J: set true (HexLive ▸ Skin Paint ▸ Log Repaint Cost) to
        // print what a repaint actually costs. Painting is main-thread-only in
        // Unity, so when it is slow the answer is always "how many targets did
        // it touch and how many did it have to CREATE" — a 2048² target with
        // mips is ~22 MB and its allocation is a driver stall.
        public static bool LogRepaintCost;
        private static int _rtCreations;

        // How many paint targets one repaint may CREATE. A 2048² target with
        // mips is ~22 MB and its allocation is a synchronous driver call — the
        // stall you feel as a freeze. A fresh wound can want three at once
        // (albedo + normal + gloss), and since 40.8-J a decal that crosses a
        // seam wants them on both sides, so a bite burst could ask for six in
        // one frame. Budgeting them spreads the cost over repaints (0.25 s
        // apart) instead of spiking; the deferred target simply paints on the
        // next pass, which is a quarter second nobody sees.
        private const int RtCreationsPerRepaint = 1;
        private int _rtBudget;

        // ---- what each slot's composite already contains ----
        //
        // A repaint used to rebuild EVERY painted slot from scratch on any
        // state change: full base blit, every stamp again, full GenerateMips.
        // So a bite on an arm also rebuilt the torso, the legs and the face for
        // nothing, and during a fight that ran four times a second. Two gates
        // now sit in front of that work:
        //   1. a per-slot signature — a slot nothing touched is skipped whole;
        //   2. an ADDITIVE path — when the only change is new stamps arriving,
        //      they are drawn straight onto the existing target instead of
        //      rebuilding it.
        // A full rebuild is still the answer to anything that REMOVES or dims
        // paint (healing, washing, a tan step), which is also the safety net:
        // if the incremental composite ever drifted, the next removal fixes it.
        private int[] _paintedSig = System.Array.Empty<int>();
        private HashSet<string>[] _paintedKeys = System.Array.Empty<HashSet<string>>();
        private Dictionary<string, float>[] _paintedAlpha =
            System.Array.Empty<Dictionary<string, float>>();
        private Color[] _paintedTone = System.Array.Empty<Color>();
        private float[] _paintedWet = System.Array.Empty<float>();

        // Stamps drawn into `slot` this pass — filled by the repaint helpers so
        // the additive gate knows what is already on the target.
        private readonly List<string> _drawnThisPass = new();
        private bool _slotDeferred;

        // Everything that decides what a slot's composite looks like, folded
        // order-independently (Dictionary order is not stable across removals,
        // so the stamps combine through xor+sum rather than a running hash).
        private int SlotSignature(int slot, bool hasAlbedo, bool hasNormal, bool hasGloss,
            bool hasDroplet)
        {
            unchecked
            {
                var h = 17;
                h = h * 31 + SkinToneHash(_appliedTone);
                h = h * 31 + Mathf.RoundToInt(_wetSmoothness * 100f);
                h = h * 31 + (hasAlbedo ? 1 : 0) + (hasNormal ? 2 : 0) +
                    (hasGloss ? 4 : 0) + (hasDroplet ? 8 : 0);

                var mixed = 0;
                var summed = 0;
                var count = 0;
                foreach (var pair in _stamps)
                {
                    if (!StampTouchesSlot(pair.Value, slot))
                    {
                        continue;
                    }

                    var alpha = _alpha.TryGetValue(pair.Key, out var a) ? a : 0f;
                    var one = pair.Key.GetHashCode() * 397 ^
                              Mathf.RoundToInt(alpha * 100f) * 31 ^
                              (pair.Value.GeometryReady ? 0x5bf03635 : 0) ^
                              (pair.Value.DrawAsRect ? 0x27d4eb2d : 0);
                    mixed ^= one;
                    summed += one;
                    count++;
                }

                h = h * 31 + mixed;
                h = h * 31 + summed;
                return h * 31 + count;
            }
        }

        // The GPU can drop a render target's contents (display sleep, device
        // reset — the "grey vinyl" bug); a slot that lost one must repaint even
        // when nothing about it changed.
        private bool SlotTargetLost(int slot)
        {
            var albedo = _slotRt[slot];
            var normal = _slotRtNormal[slot];
            var gloss = _slotRtGloss[slot];
            return (albedo != null && !albedo.IsCreated()) ||
                   (normal != null && !normal.IsCreated()) ||
                   (gloss != null && !gloss.IsCreated());
        }

        /// <summary>
        /// True when the only difference from the painted composite is NEW
        /// stamps, so they can be drawn straight onto the existing target.
        ///
        /// Refused whenever paint would have to be taken AWAY (a mark healed,
        /// dirt washed off, the tan stepped) — a composite cannot be un-drawn.
        /// Refused for droplets too: their albedo pass MULTIPLIES the
        /// destination, so a second visit would darken twice, and a wound
        /// arriving after them would sit on top of water instead of under it.
        /// And refused when a new SPECKLE shows up, because the speckle field
        /// is the bottom layer — drawing one now would put it over the wounds.
        /// </summary>
        private bool CanDrawAdditively(int slot, bool hasDroplet)
        {
            if (_forceFullRebuild || hasDroplet || _paintedKeys[slot].Count == 0 ||
                _slotRt[slot] == null)
            {
                return false;
            }

            if (SkinToneHash(_paintedTone[slot]) != SkinToneHash(_appliedTone) ||
                !Mathf.Approximately(_paintedWet[slot], _wetSmoothness))
            {
                return false;
            }

            // Nothing painted may have vanished or changed strength.
            foreach (var pair in _paintedAlpha[slot])
            {
                if (!_stamps.TryGetValue(pair.Key, out var stamp) ||
                    !StampTouchesSlot(stamp, slot) ||
                    !_alpha.TryGetValue(pair.Key, out var now) ||
                    !Mathf.Approximately(now, pair.Value))
                {
                    return false;
                }
            }

            foreach (var pair in _stamps)
            {
                if ((pair.Value.IsSpeckle || pair.Value.IsBruise) &&
                    StampTouchesSlot(pair.Value, slot) &&
                    !_paintedKeys[slot].Contains(pair.Key))
                {
                    return false;
                }
            }

            return true;
        }

        // False when this repaint could not create the target it needed —
        // the caller re-arms the dirty flag so the rest lands next pass.
        private bool TakeRtBudget()
        {
            if (_rtBudget <= 0)
            {
                // Come back next frame for the rest.
                _freshPending = true;
                _slotDeferred = true;
                return false;
            }

            _rtBudget--;
            _rtCreations++;
            return true;
        }

        private void RepaintAll()
        {
            if (_materials == null)
            {
                return;
            }

            var watch = LogRepaintCost ? System.Diagnostics.Stopwatch.StartNew() : null;
            var createdBefore = _rtCreations;
            var painted = 0;
            var additivePasses = 0;
            _rtBudget = RtCreationsPerRepaint;

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
            foreach (var slot in _paintSlots)
            {
                if (slot < 0 || slot >= _materials.Length)
                {
                    continue;
                }

                var hasAlbedo = false;
                var hasNormal = false;
                var hasDroplet = false;
                var hasGloss = false;
                foreach (var stamp in _stamps.Values)
                {
                    // A projected stamp claims EVERY slot it reaches, so a hip
                    // wrap turns on the paint target of both the leg and the
                    // torso (spec 40.8-J).
                    if (!StampTouchesSlot(stamp, slot))
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

                // Gate 1: nothing about this slot moved — its targets already
                // hold exactly this composite, so touch nothing at all.
                var signature = SlotSignature(slot, hasAlbedo, hasNormal, hasGloss, hasDroplet);
                if (signature == _paintedSig[slot] && !SlotTargetLost(slot))
                {
                    continue;
                }

                // Gate 2: can the new stamps just be drawn ON TOP of what is
                // already there? Only when nothing was removed or dimmed — and
                // only for layers that stack cleanly (see CanDrawAdditively).
                var additive = CanDrawAdditively(slot, hasDroplet);
                _slotDeferred = false;

                // Tell NpcActorView which slots hold painted albedo: it pins
                // _BaseColor white on those (the tan is baked into the texture
                // here), and keeps the cheap _BaseColor tan tint everywhere else.
                _albedoLive[slot] = hasAlbedo;
                if (hasAlbedo)
                {
                    RepaintSlot(slot, additive);
                }
                else if (_slotRt[slot] != null)
                {
                    _materials[slot].SetTexture("_BaseMap", _originalAlbedo[slot]);
                }

                if (hasNormal)
                {
                    RepaintSlotNormal(slot, additive);
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

                if ((hasDroplet && _dropletStamp != null) ||
                    (hasGloss && (_glossStamp != null || _projectedStamp != null)))
                {
                    RepaintSlotGloss(slot, additive);
                }
                else if (_glossLive[slot])
                {
                    RestoreSlotGloss(slot);
                }

                // Record what the target now holds, for the next pass's gates.
                // A repaint the render-target budget cut short is NOT recorded
                // — otherwise the signature would say "already painted" and the
                // deferred half would never arrive.
                if (!additive)
                {
                    _paintedKeys[slot].Clear();
                    _paintedAlpha[slot].Clear();
                }

                foreach (var key in _drawnThisPass)
                {
                    _paintedKeys[slot].Add(key);
                    _paintedAlpha[slot][key] = _alpha.TryGetValue(key, out var a) ? a : 0f;
                }

                _drawnThisPass.Clear();
                if (_slotDeferred)
                {
                    // Half-built: forget what was recorded so the next pass
                    // rebuilds in full rather than adding onto a bare target.
                    _paintedKeys[slot].Clear();
                    _paintedAlpha[slot].Clear();
                }

                _paintedSig[slot] = _slotDeferred ? 0 : signature;
                _paintedTone[slot] = _appliedTone;
                _paintedWet[slot] = _wetSmoothness;

                if (watch != null && (hasAlbedo || hasNormal || hasGloss))
                {
                    painted++;
                    if (additive)
                    {
                        additivePasses++;
                    }
                }
            }

            if (watch != null)
            {
                Debug.Log($"[SkinPaint] npc{_npcId} repaint {watch.Elapsed.TotalMilliseconds:0.00} ms, " +
                          $"{painted} targets ({additivePasses} additive), " +
                          $"{_rtCreations - createdBefore} newly created, " +
                          $"{_stamps.Count} stamps");
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

        // ---- Spec 40.8-J: drawing a projected (seam-free) stamp ----

        private static readonly Rect FullSourceRect = new(0f, 0f, 1f, 1f);

        // True while this slot can run the projected path at all.
        private bool CanProject(int slot) =>
            _posMaps != null && _projectedStamp != null &&
            _posMaps.PositionMapOf(slot) != null && _posMaps.NormalMapOf(slot) != null;

        // The slot-UV window the decal sweeps — resolved once on the worker
        // thread (see ResolveGeometry) and only looked up here.
        private static bool TryProjectedWindow(Stamp stamp, int slot, bool underlay,
            out Rect uvWindow)
        {
            uvWindow = default;
            var slots = stamp.Slots;
            var windows = underlay ? stamp.UnderWindows : stamp.Windows;
            if (slots == null || windows == null)
            {
                return false;
            }

            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] == slot)
                {
                    uvWindow = windows[i];
                    return uvWindow.width > 0f && uvWindow.height > 0f;
                }
            }

            return false;
        }

        private void ConfigureProjectedMaterial(Stamp stamp, int slot, float fade, bool underlay,
            in Rect uvWindow, Texture? under, float underFade)
        {
            var mat = _projectedStamp!;
            mat.SetTexture(PosMapId, _posMaps!.PositionMapOf(slot));
            mat.SetTexture(NrmMapId, _posMaps.NormalMapOf(slot));
            // Quad UV -> slot UV, the DropletStamp convention.
            mat.SetVector(SlotRectId,
                new Vector4(uvWindow.x, uvWindow.y, uvWindow.width, uvWindow.height));
            mat.SetMatrix(ObjectToDecalId, underlay ? stamp.UnderToDecal : stamp.ObjectToDecal);
            mat.SetVector(DecalNormalId, stamp.DecalNormal);
            mat.SetFloat(FadeId, fade);
            // The underlay spreads 1.6x wider, so its slab has to as well or
            // the halo would be cut short of the art it is haloing.
            var depth = underlay ? stamp.Depth * 1.6f : stamp.Depth;
            mat.SetFloat(DepthId, depth);
            mat.SetFloat(DepthFeatherId, depth * ProjectedDepthFeather);

            // Second layer of the same draw (the blood halo). _UnderFade 0
            // switches it off without a shader variant.
            var underDepth = stamp.Depth * 1.6f;
            mat.SetTexture(UnderTexId, under != null ? under : Texture2D.blackTexture);
            mat.SetMatrix(UnderToDecalId, stamp.UnderToDecal);
            mat.SetFloat(UnderFadeId, under != null ? underFade : 0f);
            mat.SetFloat(UnderDepthId, underDepth);
            mat.SetFloat(UnderDepthFeatherId, underDepth * ProjectedDepthFeather);
        }

        // The window in the pixel matrix RepaintSlot installs (y flips).
        private static Rect TargetRectOf(in Rect uvWindow) => new(
            uvWindow.x, 1f - uvWindow.y - uvWindow.height, uvWindow.width, uvWindow.height);

        private void DrawProjected(Stamp stamp, Texture art, int slot, float fade, bool underlay,
            int pass, Texture? under = null, float underFade = 0f)
        {
            // With a halo in the same draw the sweep must cover the WIDER of
            // the two footprints, or the halo would be cut off at the art's
            // edge.
            if (!TryProjectedWindow(stamp, slot, underlay || under != null, out var window))
            {
                return; // the decal reaches nothing on this texture
            }

            ConfigureProjectedMaterial(stamp, slot, fade, underlay, window, under, underFade);
            Graphics.DrawTexture(TargetRectOf(window), art, FullSourceRect,
                0, 0, 0, 0, Color.white, _projectedStamp, pass);
        }

        private void DrawProjectedAlbedo(Stamp stamp, int slot, float alpha)
        {
            if (!stamp.GeometryReady)
            {
                return; // still on the worker; the next repaint picks it up
            }

            if (!CanProject(slot))
            {
                if (!_projectedShaderWarned)
                {
                    _projectedShaderWarned = true;
                    Debug.LogWarning($"[SkinPaint] npc{_npcId} slot={slot}: ProjectedStamp shader " +
                                     "or position map missing — seam-free decal skipped");
                }

                return;
            }

            // Same layering as the rect path — the pale blood-splash halo
            // beneath the detailed art — but composited in ONE pass: they share
            // an anchor, so they cover the same texels and can share the read
            // of the position map instead of sweeping it twice.
            if (stamp.Over != null)
            {
                DrawProjected(stamp, stamp.Over, slot, alpha, underlay: false, pass: 0,
                    under: stamp.Under, underFade: alpha * 0.5f);
            }
            else if (stamp.Under != null)
            {
                // Halo with no art on top (should not happen, but a bandage
                // whose PNG has not imported yet would land here).
                DrawProjected(stamp, stamp.Under, slot, alpha * 0.5f, underlay: true, pass: 0);
            }
        }

        private void DrawProjectedGloss(Stamp stamp, int slot, float alpha)
        {
            if (!stamp.GeometryReady || stamp.OverGloss == null || !CanProject(slot))
            {
                return;
            }

            _projectedStamp!.SetFloat(GlossMaxId, WoundWetGloss);
            DrawProjected(stamp, stamp.OverGloss, slot, alpha, underlay: false, pass: 1);
        }


        // Already on this slot's target? Then an additive pass must not draw
        // it again (a second visit double-darkens a multiply and re-blends an
        // alpha). Everything drawn is recorded so the next pass knows.
        private bool SkipAlreadyPainted(int slot, bool additive, string key)
        {
            if (additive && _paintedKeys[slot].Contains(key))
            {
                return true;
            }

            _drawnThisPass.Add(key);
            return false;
        }

        private void RepaintSlot(int slot, bool additive)
        {
            var source = _originalAlbedo[slot];
            if (source == null || _materials == null)
            {
                return;
            }

            var rt = _slotRt[slot];
            // A target created this very pass carries no base layer yet, so
            // there is nothing to add ON TOP of — it must be built in full.
            additive &= rt != null;
            if (rt == null)
            {
                if (!TakeRtBudget())
                {
                    return; // next repaint creates it — see RtCreationsPerRepaint
                }

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

            // Fresh copy of the authored skin — TANNED: the tan/sunburn/grime
            // tone multiplies the base here (SkinTintBlit) so it lands UNDER
            // the stamps, then every live stamp draws on top at its true
            // colour (a bandage stays cream, a wound stays red, instead of
            // being browned by the tan). Fading is just repainting with lower
            // alpha. The RT reaches the material ONLY after the base copy
            // landed: if anything below throws, the skin keeps its original
            // texture instead of showing an unfilled (black) target.
            var previous = RenderTexture.active;
            try
            {
                // Additive: the base and every earlier stamp are already on
                // the target — only the new arrivals are drawn (see
                // CanDrawAdditively). Rebuilding costs a full-target blit plus
                // every stamp again, four times a second during a fight.
                if (!additive)
                {
                    if (_skinTintBlit != null)
                    {
                        _skinTintBlit.SetColor(SkinTintColorId, _appliedTone);
                        Graphics.Blit(source, rt, _skinTintBlit);
                    }
                    else
                    {
                        Graphics.Blit(source, rt); // shader missing: untinted base
                    }
                }

                RenderTexture.active = rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, 1f, 1f, 0f); // (0,0) top-left, UV v flips below

                // §40.8-H r11: СИНЯКИ идут в самый низ — глубже кровяных
                // пятен: синяк разлит ПОД кожей, кровь лежит НА ней.
                foreach (var stamp in _stamps.Values)
                {
                    if (!stamp.IsBruise || stamp.Slot != slot || stamp.Over == null ||
                        !_alpha.TryGetValue(stamp.Key, out var bruiseAlpha) || bruiseAlpha <= 0.01f)
                    {
                        continue;
                    }

                    if (SkipAlreadyPainted(slot, additive, stamp.Key))
                    {
                        continue;
                    }

                    var bcx = stamp.Uv.x;
                    var bcy = 1f - stamp.Uv.y;
                    GL.PushMatrix();
                    GL.MultMatrix(Matrix4x4.Translate(new Vector3(bcx, bcy, 0f)) *
                                  Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, stamp.RotationDeg)) *
                                  Matrix4x4.Translate(new Vector3(-bcx, -bcy, 0f)));
                    Graphics.DrawTexture(new Rect(bcx - stamp.UvSizeX * 0.5f, bcy - stamp.UvSizeY * 0.5f,
                            stamp.UvSizeX, stamp.UvSizeY),
                        stamp.Over, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, BruiseTint(bruiseAlpha));
                    GL.PopMatrix();
                }

                // Spec 40.8-H: the zone-damage speckle field goes down FIRST —
                // wounds, bandages and droplets all land on top of the bruised
                // base. Explicit pass because Dictionary iteration order after
                // removals is not layering order.
                foreach (var stamp in _stamps.Values)
                {
                    if (!stamp.IsSpeckle || stamp.Slot != slot || stamp.Over == null ||
                        !_alpha.TryGetValue(stamp.Key, out var speckleAlpha) || speckleAlpha <= 0.01f)
                    {
                        continue;
                    }

                    if (SkipAlreadyPainted(slot, additive, stamp.Key))
                    {
                        continue;
                    }

                    var scx = stamp.Uv.x;
                    var scy = 1f - stamp.Uv.y;
                    // r4: seeded spin around the stamp centre — a GL matrix
                    // because Graphics.DrawTexture has no rotation of its
                    // own. Push/pop per stamp keeps the pixel matrix intact
                    // for everything painted after.
                    GL.PushMatrix();
                    GL.MultMatrix(Matrix4x4.Translate(new Vector3(scx, scy, 0f)) *
                                  Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, stamp.RotationDeg)) *
                                  Matrix4x4.Translate(new Vector3(-scx, -scy, 0f)));
                    Graphics.DrawTexture(new Rect(scx - stamp.UvSizeX * 0.5f, scy - stamp.UvSizeY * 0.5f,
                            stamp.UvSizeX, stamp.UvSizeY),
                        stamp.Over, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, SpeckleTint(speckleAlpha));
                    GL.PopMatrix();
                }

                foreach (var stamp in _stamps.Values)
                {
                    if (stamp.IsSpeckle || stamp.IsBruise || stamp.IsBleed ||
                        !StampTouchesSlot(stamp, slot) ||
                        !_alpha.TryGetValue(stamp.Key, out var alpha) || alpha <= 0.01f)
                    {
                        continue;
                    }

                    if (SkipAlreadyPainted(slot, additive, stamp.Key))
                    {
                        continue;
                    }

                    // Spec 40.8-J: the seam-free stamp covers the whole target
                    // and decides per texel, so it cannot be expressed as a
                    // rect — it gets its own pass. Unless the fresh lane is
                    // still carrying it, in which case it IS a rect.
                    if (stamp.IsProjected && !stamp.DrawAsRect)
                    {
                        DrawProjectedAlbedo(stamp, slot, alpha);
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

                // §118.2: кровь сквозь бинт — ПОСЛЕ обмотки и до капель.
                // Обмотка кладётся на весь тайл, поэтому в общем проходе
                // порядок словаря решал бы, видно пятно или нет; отдельный
                // проход делает «поверх» гарантией, а не везением.
                foreach (var stamp in _stamps.Values)
                {
                    if (!stamp.IsBleed || stamp.Over == null ||
                        !StampTouchesSlot(stamp, slot) ||
                        !_alpha.TryGetValue(stamp.Key, out var bleedAlpha) || bleedAlpha <= 0.01f)
                    {
                        continue;
                    }

                    if (SkipAlreadyPainted(slot, additive, stamp.Key))
                    {
                        continue;
                    }

                    var bx = stamp.Uv.x;
                    var by = 1f - stamp.Uv.y;
                    Graphics.DrawTexture(
                        new Rect(bx - stamp.UvSizeX * 0.5f, by - stamp.UvSizeY * 0.5f,
                            stamp.UvSizeX, stamp.UvSizeY),
                        stamp.Over, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, StampTint(bleedAlpha));
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

                        if (SkipAlreadyPainted(slot, additive, stamp.Key))
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
                // GPU-side writes do not advance Texture.updateCount by
                // themselves. CharacterDollStage watches that revision so a
                // frozen portrait can retake exactly one frame when this
                // face/torso/limb slot finishes painting.
                rt.IncrementUpdateCount();
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
        private void RepaintSlotGloss(int slot, bool additive)
        {
            // Any stamp material serves: droplets need _dropletStamp, rect
            // wound gloss _glossStamp, projected wounds _projectedStamp —
            // per-stamp guards below.
            if (_materials == null || _materials[slot] == null ||
                (_dropletStamp == null && _glossStamp == null && _projectedStamp == null) ||
                !_materials[slot].HasProperty("_MetallicGlossMap"))
            {
                return;
            }

            var rt = _slotRtGloss[slot];
            additive &= rt != null;
            if (rt == null)
            {
                if (!TakeRtBudget())
                {
                    return;
                }

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
                if (!additive)
                {
                    GL.Clear(false, true, new Color(0f, 0f, 0f, _wetSmoothness));
                }

                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, 1f, 1f, 0f);

                foreach (var stamp in _stamps.Values)
                {
                    if (!StampTouchesSlot(stamp, slot) ||
                        !_alpha.TryGetValue(stamp.Key, out var alpha) || alpha <= 0.01f)
                    {
                        continue;
                    }

                    if (SkipAlreadyPainted(slot, additive, stamp.Key))
                    {
                        continue;
                    }

                    if (stamp.IsProjected && !stamp.DrawAsRect)
                    {
                        DrawProjectedGloss(stamp, slot, alpha);
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
                rt.IncrementUpdateCount();
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
        private void RepaintSlotNormal(int slot, bool additive)
        {
            if (_materials == null || _materials[slot] == null ||
                !_materials[slot].HasProperty("_BumpMap"))
            {
                return;
            }

            var source = _originalNormal[slot];
            var rt = _slotRtNormal[slot];
            additive &= rt != null;
            if (rt == null)
            {
                if (!TakeRtBudget())
                {
                    return;
                }

                var w = source != null
                    ? Mathf.Min(source.width, MaxNormalRenderTextureSize)
                    : MaxNormalRenderTextureSize;
                var h = source != null
                    ? Mathf.Min(source.height, MaxNormalRenderTextureSize)
                    : MaxNormalRenderTextureSize;
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
                if (!additive)
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
                }

                RenderTexture.active = rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, 1f, 1f, 0f);

                foreach (var stamp in _stamps.Values)
                {
                    // Projected stamps carry no relief (TryPlaceProjected) —
                    // the guards below skip them, this keeps the intent plain.
                    if (stamp.IsProjected || !StampTouchesSlot(stamp, slot) ||
                        !_alpha.TryGetValue(stamp.Key, out var alpha) || alpha <= 0.01f)
                    {
                        continue;
                    }

                    if (SkipAlreadyPainted(slot, additive, stamp.Key))
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
                        // atlas; wound art keeps the default full rect. Wounds
                        // blend their relief SUBTLY (WoundReliefStrength) so the
                        // wet gloss catches light without flaring seam ridges;
                        // droplets keep their full dome.
                        var reliefAlpha = stamp.IsDroplet ? alpha : alpha * WoundReliefStrength;
                        Graphics.DrawTexture(new Rect(cx - stamp.UvSizeX * 0.5f, cy - stamp.UvSizeY * 0.5f,
                                stamp.UvSizeX, stamp.UvSizeY),
                            stamp.OverNormal, stamp.CellRect, 0, 0, 0, 0, StampTint(reliefAlpha));
                    }
                }

                GL.PopMatrix();
                RenderTexture.active = previous;
                rt.GenerateMips();
                rt.IncrementUpdateCount();
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
            SkinPaintScheduler.Unregister(this);

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

            // `body.materials` handed us INSTANCES (one per submesh), and Unity
            // does not free those with the renderer — they outlive the actor as
            // orphans, dragging their textures along. The stamp materials
            // (_skinTintBlit, _glossStamp, …) are STATIC and shared: they must
            // not be destroyed here.
            if (_materials != null)
            {
                foreach (var material in _materials)
                {
                    if (material != null)
                    {
                        Destroy(material);
                    }
                }

                _materials = null;
            }
        }

        private static float NextRand(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) / 16777216f;
        }

        // Quantized skin-tone key for the repaint state hash — ~5 bits per
        // channel, so slow tan/sunburn/grime drift crosses a bucket every few
        // seconds rather than forcing a repaint every frame.
        private static int SkinToneHash(Color c)
        {
            var r = Mathf.RoundToInt(Mathf.Clamp01(c.r) * 31f);
            var g = Mathf.RoundToInt(Mathf.Clamp01(c.g) * 31f);
            var b = Mathf.RoundToInt(Mathf.Clamp01(c.b) * 31f);
            return (r << 10) | (g << 5) | b;
        }
    }
}
