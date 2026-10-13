# Hair source-layer comparison — 2026-10-11

**Superseded visual evidence:** the original preview disabled names ending in
`_LOD1`, but actual prefab children are named `_LOD1(Clone)`. Both the candidate
and old opaque distant mesh were therefore rendered. All images without
`-SingleLOD-` in their filenames, and image metrics derived from them, are
contaminated and must not justify geometry budgets or source appearance claims.
The audit now disables every hair renderer except the chosen one by reference
and asserts exactly one enabled renderer. Initial corrected Eilis/Hair07 captures
remove the brown opaque plates. Full 32-LOD recapture is required. The static
`outer_cards` opaque-ray assumption remains a valid code observation; the
comparative budget/shape claims below are historical and need revalidation.

Local development base: a4cf3766bdb116f4d212676829fcc8c4a2c8e711.
This is candidate evidence, not an installed fix or release approval.

The supplied author's `outer_cards` chooses whole cards using first-hit rays
and explicitly assumes opaque rendering. That assumption does not hold when
restoring cutout masks: inner layers can become visible through outer alpha.
The source simplification also removes most curvature before distant LOD creation.
Rebuilding only the distant LOD cannot restore that earlier loss.

Native Unity comparisons use the same Marta binding, original material textures,
cutoff 0.42, lighting and four angles. Full Tootsie source has 255946 triangles.
The following candidates were rendered successfully; none was installed:

| Candidate | Actual triangles | Source treatment |
|---|---:|---|
| FarCandidate | 949 | Existing pruned LOD0, weld 1 cm, collapse |
| Far1800Candidate | 1800 | Existing pruned LOD0, weld 0.1 cm, collapse |
| Full950 | 1344 | Original layers retained; requested 950 cannot be reached |
| Full2400 | 2400 | Original layers retained, original UV retained |
| Full4000 | 4000 | Original layers retained, original UV retained |
| Full8000 | 8000 | Original layers retained, original UV retained |

Full4000/8000 retain visibly rounder curls, particularly from behind. Full2400
and the far variants remain angular. Pure planar dissolution with UV/material
boundaries preserved leaves 79004 triangles even at 15 degrees, so it is not
a suitable standalone low-poly solution. Hair07's broad strips also occur in
the full original reference; their presence alone is not proof of a reduction bug.

An indicative fixed head-crop image comparison is stored in
`Build/MSIVisualAudit/HairSourceUV/tootsie-candidate-comparison.json`.
It includes face/neck, is sensitive to dark foreground segmentation, and must
not be treated as an exact hair silhouette or perceptual-quality metric.
Across four views, foreground IoU rises from 0.632 (949-triangle candidate)
to 0.770 (Full4000), and RGB MAE falls from 8.38 to 4.79. Full8000 is closer still.

Evidence: `unity-hair-full-references.log`, `unity-hair-full-budgets.log`,
`unity-hair-full-low-budgets.log`, and matching `HairSourceUV/*source-uv-*.png`.
All native jobs finished with exit 0. Source blend and production assets were
not overwritten. Next: select curvature-preserving budgets per style, retain
source UV and scalp/base/cap, validate both LODs for all 16 styles and animation,
then integrate atomically and hand off for Mac WebGL/live QA.
