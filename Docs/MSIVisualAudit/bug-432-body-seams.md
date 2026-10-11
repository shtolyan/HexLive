# Body atlas seams (Flashback 432)

The player's thin black lumbar stripe in the secondary-motion video came from
black texels along the albedo UV boundary. Hair-off and normal-map-off captures
retain it; a uniform grey albedo removes it. The remaining broad spine shading
is geometry, not the black stripe. A weight-repair hypothesis was rejected:
all 513 duplicate skin-vertex groups on Marta stay coincident in the sampled
running pose (maximum gap zero); no bone weights were changed.

`Tools/pad_body_atlas.py` rasterizes the exported Unity UVs at source resolution,
removes a two-pixel contaminated boundary from the colour seeds, then propagates
interior colour through a 24-pixel gutter. It changes neither vertices nor UVs.
PNG preserves the inner colour pixels byte-for-byte and avoids another JPEG
compression pass. The two original texture GUIDs are retained, so all existing
complexion materials still reference the corrected texture. Source manifest
paths and SHA-256 values are updated. Import settings are retained.

Original inputs (recoverable from parent commit 7a6783d):

- Marta: `Marta_BaseColor_2048_0b51d48c5148.jpg`, SHA-256
  `0b51d48c5148236330efaa27aa18203811d79ff86a63319af03ff7a6981c0cd7`.
- Kshishtof: `Kshishtof_BaseColor_2048_e32e5c0944e4.jpg`, SHA-256
  `e32e5c0944e42078e0bf427eed5ffb91b0d2138c9513fb54b3a0cb409a088096`.

Reproduction: export current UV/triangle data with
`HexLive.UnityPresentation.Wearing.Editor.PeopleSkinNormalExport.Export`, then
run `python Tools/pad_body_atlas.py --source <original.jpg> --mesh
Build/PeopleSkinNormalSource/<actor>-mesh.json --output <padded.png>`.
Original input copies on MSI are in `Build/MSIVisualAudit/AtlasSource`.
Do not feed a previously padded result back into the generator.

Offline validation: 5,562 torso/limb seam samples on Marta, near-black samples
14 -> 0; minimum mean RGB 0.014 -> 0.411. Kshishtof: 5,112 samples, 0 -> 0.
These exclude mouth/eyelids and sole contact boundaries; they do not claim
every dark texture detail is a defect. 1,753,311 / 1,869,659 inner atlas pixels
are exactly unchanged for Marta / Kshishtof.

Native Unity evidence uses `MsiSecondaryMotionDemo.AuditSeams`:
`MsiBodyAtlasAudit.Capture` checks every complexion reference and renders both
bodies in idle/run/jump from four sides, before and after. The running split
view then isolates hair, normals and albedo. Evidence lives under
`Build/MSIVisualAudit/SeamAudit`, outside committed assets. This fixture now
places the baseline on screen left (camera looks along -Z); the earlier
secondary-motion clip's OFF/ON labels had been reversed by world-X placement.

This fixes body colour-atlas seams. The hair-physics prototype's strand/body
intersection is a separate issue. No server, bundle or live-game publication
is part of this change; published-game validation remains required.

Final verification, Unity 6000.4.5f1: native process exit 0; all 10 base/complexion
material references passed; 48 before/after images and 284 running/jumping
comparison frames captured. All 24 corrected pose/view images were reviewed.
The pose audit executes in Play Mode with AlwaysAnimate and fails if the hand
stays at the rest-pose position. The earlier edit-mode T-pose run was rejected.
The final split view is `frames/frame-0281.png`: left original, right corrected,
hair hidden, authored normal map enabled. Padding regression tests: 2/2 pass.
