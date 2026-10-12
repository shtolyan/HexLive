# MSI visual audit — 2026-10-11

Status update: Tolya explicitly authorized isolated hair/material work on this base.
The earlier coordination hold is superseded. None of HV-001…005
is fixed, ready_for_live_QA, or verified. No Unity session/build was launched.
Requirements and assignment are copied alongside this report from Sveta's VPS.

## Coordination and checkout

- ACK for all five bugs delivered through tell_sveta.sh, exit 0 and explicit
  `передано Свете (режим: fyi)` acknowledgement.
- Original checkout: C:/Users/shtolyan/Documents/GitHub/HexLive, master,
  91300fba189d4094e3fd4b6216ae45b3e8d360f7. Existing AGENTS.md,
  HexLiveReleaseBuilder.cs, ProjectSettings.asset and TestResults/ preserved.
- Origin: https://github.com/shtolyan/HexLive.git. Remote HEAD was master at
  820f7ca7b674dc6e5c8d2df9293f4258c8e3084a when checked and fetched.
- Isolated worktree: Build/msi-visual-bugs-20261011; branch
  sveta/msi-visual-bugs-20261011, exact base 820f7ca7b674dc6e5c8d2df9293f4258c8e3084a.
  It was clean after checkout; only this audit documentation has been added.
- Sveta's update identifies Mac HEAD 4b34c09a6a3f913b29f602b77e48fc0003419cff
  and five newer commits overlapping PortraitStage, CharacterPanel, NpcActorView,
  NpcFaceAnimator and HexWorldRenderer. Await agreed published base or explicit
  acceptance of cherry-picks from MSI base with overlap review. Do not overwrite.
- Flashback whoami failed TLS verification: certificate has expired.
  No certificate bypass or tracker writes. No confirmed HV-to-Flashback mapping.
- Installed project Unity: 6000.4.5f1. No Unity.exe process found during audit.
  WebGL Support not installed; final WebGL build/publication remain on Mac.
- Current base AGENTS.md retires MCP and requires coordinated Unity CLI.
  No Unity MCP calls made. Assignment-file reads are the current reply channel;
  no continuous watcher is running.

## LFS and available sources

Git LFS 2.13.3. Checkout initially skipped smudging to inventory before downloads.
Then scoped `git lfs pull` hydrated People/Source, Resources/HexLive/Decals and
Resources/HexLive/UI/Emoji successfully. Verified no remaining pointers in these
three scopes. Source: 120 files, 53,347,012 bytes; decals: 43 files, 25,880,470 bytes;
emoji: 54 PNG files, 910,179 bytes (excluding metadata).
All 55 model SHA-256 values match source-manifest.json. Other LFS assets outside
these scopes are not yet guaranteed hydrated; do not open Unity on pointers.
Checkout warned about 80 upstream files stored as ordinary blobs despite LFS
attributes; git status remained clean. No normalization attempted.
Original Blender source is recorded on Mac at
/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Primal_Wardrobe.blend;
this audit confirms the exported FBXs, not possession of that .blend.

## HV-001 — hair

Coverage inventory: 16 hairstyles, each LOD0/LOD1, authored for Marta:
AdellHair, AsukaHair, BendineHair, Bob3Hair, ChunkyHair, EilisHair, Hair07,
JelikaHair_32434, JenniferHair, LeonyPonytail, LoonaHair, LowPonytail, Neu09Hair,
OnyxHair, ShilohHair, TootsieRollHair. Visual front/side/back coverage: NOT RUN.

PeopleAssetPreparation.cs:193 ApplyLowPolyHairMaterial clears _BaseMap and sets
opaque brown _BaseColor (alpha 1). Import uses manifest culling/alpha settings.
The 14 material rows whose names contain Hair are OPAQUE and doubleSided=true.
Example Adell LOD0 has a single AdellHair_WebGL material and WebAtlas UV.
Hypothesis: scalp and hair collapsed into opaque material during preparation;
must inspect submesh/source-face mapping before changing transparency globally.
Double-sided manifest settings alone do not prove correct face orientation.
History references: feb5b8a39 and a8cdc3964 synchronize _Color/_BaseColor;
238f4fff0 addresses hair visibility under a cap. Earlier transparent-scalp
implementation has not yet been located. No asset/runtime edits.

## HV-002 — emoji

CharacterPanel.cs uses Label(info.Emoji) for missing icons (e.g. lines 4007,
4124, 4219); crafting and HexInspectorPanel have equivalent text paths.
NpcSpeechBubble.cs already documents why bitmap-color emoji fonts render tofu
and uses bundled Resources/HexLive/UI/Emoji sprites. Its comment says these
were rasterized from a system font. Existing 54 PNGs are not evidence of full
inventory coverage or redistribution permission. No license determination made.
Next: enumerate unique symbols/VS16/ZWJ sequences and all display sites, map
coverage and unknown-symbol fallback, establish asset provenance/licensing.
Do not extract Apple font or silently substitute a different visual style.
No browser, cold-cache, OS equivalence or scaling acceptance performed.

## HV-003 — normals

205 prepared .mat files; 28 have nonzero _BumpMap, all dress/collar variants.
All eight Skin_* materials have empty _BumpMap. Source manifest has normal
image links for only four material rows (Marta/Kshishtof dress and collar),
all referencing PrDr_furNO_7674ceefeda6.jpg. Importer's normal assignment at
PeopleAssetPreparation.cs:446 sets these to strength 0.6 and enables _NORMALMAP.
This is stronger evidence of missing authored inputs than a shader-keyword-only
failure; runtime shader behavior still needs validation.
Historical references: 55a597fcf (clothing/skin normals), be5395c0e (skin relief),
69ce52945 (restore _NORMALMAP on 104 materials after missing LFS pointers).
New bodies use one atlas material per body mesh and Model_UV; legacy textures
must not be assigned blindly without validating/rebaking their UV mapping.
Next: compare old material/map strengths, texture channels, tangent basis and
new atlas bake provenance under identical lighting. No visual acceptance yet.

## HV-004 — bandages

SkinTexturePainter.cs:450 has six fixed WrapRects: Torso, Pelvis, ArmL, ArmR,
LegL, LegR. TryPlace around line 2250 explicitly assumes shared Genesis 3 UDIM
layout; it applies zone overlay and normal to point.Slot using fixed UV rects,
then returns before the later body-space projection path. New source manifest
instead records a single atlas material on each main body mesh.
Hypothesis: legacy UV rectangles are being reused against repacked body atlas.
Must verify new UV coverage and compare old/new actors in all six wrap zones,
plus every other available dressing zone, at rest and in motion. Bundled zone
normal textures exist and are hydrated; their presence does not prove runtime
normal composition or correct placement. No maps or strengths changed.
HV-003/004 must be integrated together without reverting new body content.

## HV-005 — portrait

Explicit intentional optimization found in commit
750962bb18f78a6817f4e66b60ca363ce46aa823. PortraitStage.cs:31 sets 1f/6f
only under UNITY_WEBGL && !UNITY_EDITOR. LateUpdate disables the camera until
Time.unscaledTime reaches _nextWebRenderAt and schedules next render at now +
interval. RenderTexture is 512x368; MSAA is 1 on WebGL, 2 elsewhere.
Nominal interval 166.7 ms; actual frame-quantized rate may be lower and has NOT
been measured. Editor timing cannot validate this WebGL-only branch. The commit
explains avoidance of a second scene rendering pass every frame on the browser
main thread. Do not remove the limiter without profiling and owner agreement.
Mac has newer overlapping PortraitStage changes; this finding is base-specific.
Candidate by-design, not a fix/closure: requires current Mac code review, published
build render/frame-time measurements, video and explicit owner acceptance.

## Prior gate (superseded for isolated hair/material development)

Obtain Mac base/overlap agreement via Sveta, resolve Flashback TLS/mapping where
applicable, finish source/asset audit and hydrate remaining Unity dependencies.
Then coordinate sole Unity ownership, capture reproducible before/after matrices,
make isolated fixes and focused checks, and hand off individual commits with
base/fix SHAs. Local completion status can only be ready_for_live_QA. Published
game QA is required before verification; MSI does not build/publish WebGL.

## Active work — authorization update

Tolya explicitly authorized Unity without MCP and isolated hair/material work at
820f7ca7b674dc6e5c8d2df9293f4258c8e3084a. Sveta received a started ACK (exit 0)
and updated the assignment to remove the blanket hold. Known Mac overlap files
remain excluded from this implementation.

Found historical alpha repair 75730f9c88b0e293c35c1c9bd2e76d7d7cb8ab0a.
Pillow inspection confirms all 16 new hair Atlas512 images are RGB without alpha.
Legacy HairExtractor.cs retains distinct scalp/cap materials with authored opacity.
Requested original Blender scene and bake mappings through Sveta to restore
alpha/normal data correctly on repacked UV rather than inventing transparency.

Prepared MsiPeopleVisualAudit.Capture as a native Editor CLI entry point for
96 baseline views (16 hairstyles x 2 LODs x 3 directions), using BodyBones/Wear.
Its output is under ignored Build/MSIVisualAudit/before, not production assets.
The tool has not yet been compiled or run at the time of this note.

## HV-004: measured new-atlas mismatch (MSI source audit)

`Build/MSIVisualAudit/bandage-uv-audit.json` compares the exact existing WrapRects with all valid baked surface samples in the prepared Marta/Kshishtof paint maps. Both meshes have a single paint slot (0). For Marta the legacy Torso rectangle intersects 89/128 ArmR samples and zero Torso samples; the legacy ArmL rectangle intersects 43/128 ArmR samples and zero ArmL samples. LegR rectangle intersects 97/128 ArmL and 118/128 ArmR samples, but only 1/128 LegR sample. Kshishtof exhibits the same mismatch. These are rectangle/sample intersections, not rendered alpha coverage or runtime screenshots.

Cause: SkinTexturePainter.TryPlace takes the new point.Slot but substitutes the old fixed Genesis UV rectangle and returns before projected placement. The normal map follows the same misplaced stamp. A material-only change cannot correct that mapping. Fix must supply artwork/mapping for the new atlas (including correctly rebaked tangent normals), retaining old art only as reference. No runtime file changed for this audit; targeted integration review is required before changes shared with Mac.

## 2026-10-11: original checkout, native bandage projection matrix

Current work is in the original checkout on `codex/msi-visual-bugs`. No new worktree or worker was created. The earlier worktree was removed at the player's request after preserving its changes; see `Build/MSIWorktreeMigration-20261011`. The delivered original blend and all 58 source files have been verified; source delivery is no longer a blocker. Native Unity works in this checkout. Earlier statements about waiting for a source or an uncompiled capture tool above are historical.

`Build/MSIVisualAudit/bake_bandage_matrix.py` reads the delivered blend and original body FBXs without saving them. It transfers six authored wrap color/alpha overlays to each new body atlas. The first native `CaptureBandageMatrix` run completed with exit 0 in 29.61 seconds and produced 36 views. Front views place all six zones on the appropriate body parts; rear inspection caught a remaining male Torso gap, so this is not an accepted complete fix.

The male source geometry matches the new mesh (mean nearest-surface distance 0.0172 cm, p95 0.0855 cm, max 0.3077 cm). Its spine UV reaches about U=.87 within the band, outside the old female crop ending at U=.856445. The audit baker now repeats torso fabric horizontally while preserving its authored vertical mask. It also bakes source tangent normals into the new atlas tangent basis with selected-to-active NORMAL baking; it does not copy normal RGB as color. Twelve color/alpha and twelve normal projections are stored only under `Build/MSIVisualAudit/BandageMatrix`.

Runtime integration into `SkinTexturePainter.cs`, verification of normal compositing/import settings, movement checks, and published-game QA remain. MSI requested explicit file ownership coordination with Sveta before editing this shared runtime file. No production assets were replaced by these prototypes, and Flashback #433 remains `in_progress`.
