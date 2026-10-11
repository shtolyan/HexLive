# Flashback #433 / HV-004: bandages on browser bodies

MSI branch: `codex/msi-visual-bugs`, based on approved
`820f7ca7b674dc6e5c8d2df9293f4258c8e3084a`. Mac integrates the focused commit
onto its current branch; do not replace its newer runtime files or publish from MSI.

## Change

`SkinTexturePainter` previously used Genesis crop coordinates on the new
single-atlas Marta/Kshishtof meshes. A torso wrap could paint an arm. The new
`SkinBandageAtlas` supplies six transferred color/coverage and tangent-normal
maps per body, checked against mesh name and vertex count. The painter applies
each map across the atlas; its alpha selects the intended UV islands. Missing or
stale new-layout data does not fall back to an incorrect Genesis rectangle or
round patch. Legacy bodies retain their existing mapping.

Color textures import at 2048, linear normal/coverage textures at 1024 to match
the painter's existing RT limits. Normals import as Default, not Unity NormalMap,
because stamp rendering needs unmodified RGB and coverage alpha. This does not
change medical simulation, healing, bandage selection, or the global RT budget.

## Reproduce the art

Use native Blender 5.2 with `Tools/bake_bandage_atlases.py`, passing the verified
`Primal_Wardrobe.blend` through `--source-blend`, an output directory, and
`--supersample`. The tool refuses a different source SHA. Reviewed source SHA256:
`568b871a7c839e140de973da1e670fe1ab7e00cceabab8083e0267b6bd7b5661`.
The blend is loaded without scripts and never saved. Original FBXs and wrap
PNGs come from this checkout. Twelve final PNG pairs total about 6.9 MB on disk.

The transfer normalizes temporary centimetre meshes to metre coordinates before
ray baking: insufficient local ray distances caused holes in early prototypes.
Float emission color is explicitly encoded into sRGB before byte PNG save;
alpha and tangent vectors remain linear. Supersampling filters premultiplied
color/coverage and renormalizes normal vectors. Torso fabric repeats across the
old crop's side border to close the male spine gap, which the original male
Genesis reference also exhibited.

## Evidence and remaining checks

Dependency discovered during #432: the prepared body meshes had zero tangents.
The initial tests below prove atlas placement and normal-texture production,
not visible normal-map relief. Integrate the body-tangent postprocessor fix
from #432 before accepting bandage lighting in the shared build. A normal
keyword and populated render texture alone cannot establish that acceptance.

Native Unity 6000.4.5f1 PlayMode capture through real `Construct`, `Sync`, and
`PaintCycle` passed all 12 body/zone cases and wrote 48 views, including bent
limbs. The local fixture injects prepared maps into the existing map caches
(same approach as `PeoplePaintRuntimeTests`), so it tests painting rather than
network delivery. Eye/teeth textures remained unchanged. Evidence is under
`Build/MSIVisualAudit/BandageRuntime`; log:
`Build/MSIVisualAudit/unity-bandage-playmode.log`.

The capture exited 0, but abrupt editor shutdown reported an unrelated
`PlayerAssignmentDialog.OnDisable` null reference and native-allocation warnings;
this is not claimed as a clean end-to-end game session. The separate focused
NUnit PlayMode run subsequently passed all three tests (0 failures/skips,
Unity exit 0): both bodies reject stale topology and preserve normal coverage,
and all 12 body/zone cases paint color/relief without changing eyes or teeth.
Results: `Build/MSIVisualAudit/TestResults/bandage-playmode.xml`.

The 22 existing BandageHandProp, SkinPaintLiveness and SpecStructure contract
checks passed. MSI lacks .NET 9, so those checks ran on installed .NET 10 with
process-only `DOTNET_ROLL_FORWARD=Major`; no machine runtime setting was changed.

Mac must rebuild the client bootstrap containing the new Resources assets and
the painter code together. Updating remote actor bundles alone is insufficient.
Published-game QA remains mandatory: all six zones on both actors during
movement, reference relief under lighting, no off-zone skin/eye/teeth changes,
and bootstrap/GPU memory review on the target browser. No WebGL build or release
was made on MSI. Flashback remains unconfirmed until the player tests the shared
published game.
