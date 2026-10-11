# Flashback #435: body wounds and dirt

Native Unity 6000.4.5f1 / D3D11, existing MSI checkout. Diagnostic actor Marta,
seed 713, 18 wounds. Diagnostic captures used hygiene 0.65; final wounds-only
captures disable dirt to keep the unfinished dirt work separate.

## Confirmed diagnosis

Before the fix, the live body material referenced `SkinPaint_713_0`, with no
MaterialPropertyBlock override. All 18 projected wound stamps had textures and
finished geometry. Yet the albedo only differed from its source by 369 pixels
above a 5/255 RGB threshold, with no visible wounds in the camera captures.

GPU readback of `skinpos_Marta_g0.exr` reported **RGBA32**. At a head point the
expected mesh position was `(8.46, 165.65, -3.63)` but the GPU returned
`(0.020, 1.000, 0.000)`: colour import had destroyed signed HDR coordinates.
All seven body zones showed the same lost-height/negative-coordinate problem.

Separately, UV stamp density used a fixed 1.7 mesh-unit height, although prepared
people have a baked mesh height near 180 (centimetres). This shrank blood and
bruise marks to their minimum UV clamps. The projected size calculation already
used MeshHeight; the rectangle path needed the same unit conversion.

Dirt still stretches under the shoulder. An experimental angle-fade shader
has not resolved that defect and is deliberately excluded from this wound fix.
The shared report stays in progress until that remaining part is resolved.

## Changes and validation

`SkinPositionMapImportPolicy` and the generator preserve position maps as
linear, uncompressed RGBAHalf on Standalone and WebGL. `SizeFromDensity` uses
the baked MeshHeight. Blood and bruises on packed people use surface projection
instead of UV rectangles, preserving layering without adding wound gloss.
Packed UV seams inside one material now trigger the fresh projection upgrade;
completed asynchronous geometry wakes the fresh lane instead of waiting for
the ambient repaint cycle. No healing simulation, wound placement seeds, or
authored bandage coverage is changed.

Regression fixture checks signed GPU values, actual red wound pixels on all
seven zones of Marta/Kshishtof, healing removal, unchanged eye/teeth textures,
and metre/centimetre size invariance. Native PlayMode run passed **7/7 tests**,
zero failures/skips, Unity exit 0. It includes the existing three bandage tests
(both atlas topologies and all-zone albedo/normal painting) and four new wound
regressions. Wound pixel assertions exercise the live fresh lane without an
ambient PaintCycle; blood/bruise tests also verify their removal.

Results: `Build/MSIVisualAudit/WoundTests/results.xml`; log beside it.
Native captures: `Build/MSIVisualAudit/WoundPhoto/`. Wounds are visible and
the triangular blood-island cuts from the previous capture are gone.
The Editor logs a pre-existing PlayerAssignmentDialog shutdown NullReference;
the test run itself passed. This does not establish a clean full-game run.

This fix is not in Singapore registry445 activated earlier. It requires a
matching Player (SkinTexturePainter code) and rebuilt actor content
(position texture format). Native images do not establish WebGL/live QA.
