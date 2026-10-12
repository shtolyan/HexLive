# Hair cap investigation — corrected single-LOD preview

Only filenames containing `-SingleLOD-` are valid visual evidence. No production
hair assets have been replaced. All jobs below finished with exit 0.

Shiloh's scalp JPG is fully opaque: alpha is 255 everywhere, so lowering its
cutoff cannot repair exposed skin. A source-space clearance adjustment moved
112 cap vertices by at most 0.634425 cm toward the author's 0.65 cm clearance.
None of the sampled vertices was initially inside the body; triangle interiors
and clearance, rather than a count of penetrated vertices, matter here. The
result removes the visible crown opening in the four-angle native Unity check.
Evidence: `ShilohHairCapCandidateLOD0-SingleLOD-source-uv-*.png` and
`cap-candidate-report.json` under `Build/MSIVisualAudit/HairSourceUV`.
This still needs normal recomputation, animation and final import verification.

Bendine and Chunky cap maps are 256-square RGBA with soft alpha edges. Lowering
only cap cutoff to 0.02 did not resolve the coarse strands. Reducing strand
cutoff to 0.02 or 0.005 also did not materially restore their shape. Those
changes are not accepted fixes. Existing single/double-underscore texture
variants have almost identical alpha; their RGB/resolution differs, so they
must not be substituted merely on filename similarity.

Full original references were recaptured with only one hair renderer enabled:
Bendine 321772 triangles, Chunky 200940 triangles. They have smoother authored
strands than the reduced candidates. Increasing reduced budgets to roughly
12000/6000 triangles did not adequately reproduce them. Keeping original
vertex splits instead of welding coincident vertices at 0.00015 cm also did
not solve this. Evidence logs: `unity-cap-full-references.log`,
`unity-hair-strand-cutoffs.log`, `unity-hair-curly-candidates.log`, and
`unity-hair-seam-candidates.log`.

Next: use per-card curvature/coverage constraints instead of distributing one
global collapse budget over all layers. Keep original UVs and cap geometry;
do not accept a nominal triangle count or successful render as visual proof.
Other 14 styles retain the existing corrected 128-view candidate matrix.
# Alpha-aware card selection follow-up

Two subsequent controls completed in Blender and Unity with exit 0:

- `AlphaUnshiftedLOD*` keeps exactly the alpha-selected strand geometry, but
  disables the 0.65 cm cap displacement. Chunky's rear still lacks strand
  coverage and additionally exposes skin. Cap displacement alone is not the fix.
- `CurlyUnfittedLOD*` keeps all source layers at the earlier 12000/6000 triangle
  budgets but disables `fit_scalp` on nearby strand vertices. Chunky LOD0 rear
  coverage returns; Bendine LOD0 front remains visibly angular with a crown gap.
  This supports removing indiscriminate strand fitting, but is not acceptance of
  either full hairstyle. Logs: `unity-hair-alpha-unshifted.log` and
  `unity-hair-curly-unfitted.log`. Both render only one hair LOD at a time.

The regional candidate reserves selection independently above/below the source
cap's minimum height, rather than letting long tails dominate ray votes. Native
Unity captures completed with exit 0 (`unity-hair-regional-cards.log`); all sixteen
frames reviewed in `HairSourceUV/regional-contact.jpg`. Chunky's rear strand
coverage is restored and Bendine's front crown gap is no longer visible. Both
styles keep recognizable silhouettes in all four views. Some coarsening remains
at LOD1; this is a candidate for integration, not animation/live acceptance.
Actual triangles: Bendine 11999/5999, Chunky 12000/6000. Cap is fitted separately;
individual strands retain their source positions before decimation.

`hair_selected_manifest.py` froze 32 inputs (16 styles, both LODs), checked all
weights, indices, finite positions, source texture existence and 128 SingleLOD
capture paths, and recorded SHA256 fingerprints. Bendine/Chunky use regional
selection; Shiloh uses the cap-clearance candidate; the other thirteen use the
all-layer candidates. Output: `HairSourceUV/selected-integration-inputs.json`.
Combined triangles across all styles: LOD0 79986, LOD1 45657. This validation does
not cover final FBX import, skeleton reuse, animation or browser performance.

`hair_alpha_card_candidates.py` sampled 26 directions with 40x40 rays each,
testing original texture alpha before accepting a hit. Both styles retain cap
components; LOD0 keeps 400 ranked strand components and LOD1 keeps 250.
`unity-hair-alpha-cards.log` completed with exit 0. Actual triangle counts:
Bendine 7999/3999, Chunky 8000/3999.

Single-LOD front comparison of Bendine against the full source retains a broadly
similar silhouette. Chunky rear comparison exposes substantially more plain cap
than the full source reference. Therefore this selection is not accepted as a
complete fix and has not been installed into production assets. Global visibility
ranking needs a regional coverage constraint; more triangles alone did not resolve
the earlier candidates. Both LODs and all sides still require validation.

