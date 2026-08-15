---
name: hexlive-furniture-authoring
description: Author, export, integrate, or repair HexLive furniture and §120 architecture elements shared by Blender and Unity. Use for furniture or architecture models/FBXs; walls, doors, windows, corner supports, floor and roof sectors, hearths, beds, wardrobes, racks, collectors, workbenches; pivot, scale, axis, orientation, 60-degree rotation, hex/junction footprint, wall alignment, construction stages, Blender-to-Unity import, HutLayoutDesigner and the building constructor, dark or wrong-looking materials, mirrored or misplaced sector art, and test-versus-production placement mismatches.
---

# HexLive furniture authoring

Keep art, placement data, and runtime rendering under one repeatable contract. Fix an axis defect in the source/exported asset; never accumulate per-model rotation compensation in gameplay code.

## Start from the production path

1. Read `AGENTS.md`, the relevant sections of `CLAUDE.md` and `spec.md`, and the current factory/loader before editing.
2. Read [references/project-map.md](references/project-map.md) when locating the relevant source, exporter, layout data, or runtime path.
3. Treat an editable scene or `PlayerPrefs` layout as a draft only. Find the committed simulation/blueprint data that the ordinary game loads.
4. If the task changes spatial behavior, calculate it from `HexPointLayout` and `HexSpatialMath` and provide the diagram required by `CLAUDE.md`.

## Preserve one furniture coordinate contract

- Author in world units at 1:1 scale.
- Use Blender `+Z` as up. It becomes Unity `+Y` through the normal FBX import.
- Put the code-facing pivot on the floor at the central/primary occupied junction.
- Use Blender local `+Y` as the primary elongated/occupied axis, matching `bed.basic`. In Unity this is the local footprint line on `Z`; its sign is irrelevant for a symmetric footprint.
- Bake artistic transforms into mesh data. Export one clean root with zero position/rotation and unit scale.
- Allow simulation yaw only at `0° + 60°k`. Apply `SimulationUnityMapper.ToUnityFootprintYawDegrees`, never character-forward yaw, to the identity presentation root.
- Keep Blender/FBX import transforms on a child model. The runtime wrapper stays identity, as in `BedAssembly`.
- Store footprint nodes, interaction nodes, wall side, and approved yaw in data. Do not infer them again from renderer bounds or camera-facing geometry.

Do not add asset-id branches, hidden `30°`/`90°` corrections, scene transform overrides, or a second coordinate converter. If one asset needs one, its source/export basis is still wrong.

## Author and export

1. Resolve the real footprint and desired wall from committed layout constants.
2. Position the artistic model around the intended floor pivot in Blender.
3. Normalize the export basis in the exporter, not by rotating the review scene hierarchy until it looks right.
4. Export only the furniture module. Exclude review beds, walls, garments, markers, cameras, and lights.
5. Use Blender FBX settings `axis_forward='-Z'`, `axis_up='Y'`, applied unit scale, no animation unless the asset explicitly owns animation.
6. Preserve approved names needed by construction-stage code. Treat their schema as runtime data, not decoration.
7. Run the bundled verifier before opening Unity:

```bash
/Applications/Blender.app/Contents/MacOS/Blender -b --python \
  .agents/skills/hexlive-furniture-authoring/scripts/verify_furniture_fbx.py -- \
  --fbx Assets/Resources/HexLive/Objects/<asset>.fbx \
  --profile standing
```

Use `--profile low` for beds and other low furniture, or `--profile any` for symmetric pieces. The verifier rejects dirty roots, non-floor pivots, missing geometry, wrong primary axis, and obvious scale/orientation failures.

## Integrate without a special case

1. Make the factory return an identity wrapper with the imported model as a child.
2. Place the wrapper at the saved junction and apply the shared footprint yaw.
3. Place sockets, garments, particles, doors, and interaction markers in that same local basis.
4. Make the test fixture call the same simulation bootstrap, factory, and renderer as the ordinary game. Do not hand-place a visually equivalent copy in the scene.
5. Promote an approved designer draft into committed blueprint/constants. The production scene must not read the designer's `PlayerPrefs` as authoritative world data.

## Promote an approved designer save

When the player says that an item is placed and the layout is saved:

1. Extract the latest explicit `[HutDesigner][SAVED]` payload instead of reading an arbitrary `PlayerPrefs` state:

```bash
python3 .agents/skills/hexlive-furniture-authoring/scripts/extract_latest_hut_layout.py \
  --type wardrobe
```

2. Promote only the element type the player just approved. Do not copy duplicate or stale beds, bays, or other furniture from the same draft unless the player approved those too.
3. Resolve `localX/localZ` from the saved junction through the canonical `HexPointLayout` grid. Treat the four-decimal log values as display values, not a new geometry definition.
4. Write the junction-derived coordinates and six-way yaw into the committed blueprint/constants used by `BuildingBootstrap`.
5. Update `spec.md` and any checked-in spatial diagram in the same change. The diagram must be generated from the committed numbers and must name the pivot and occupied-node line.
6. Verify the ordinary game and the test fixture both consume those committed values. A designer save is not integrated merely because its preview looks correct.

If `--type` finds more than one item, the extractor fails deliberately. Resolve which instance the player approved before changing production data.

For the one-hex home wardrobe, regenerate the checked-in diagram after changing `BuildingRules`:

```bash
python3 .agents/skills/hexlive-furniture-authoring/scripts/generate_wardrobe_placement_svg.py
```

## Architecture elements (§120 constructor)

Walls, windows, doors, corner supports, floor sectors, roof sectors and the
indoor hearth are one family. Their geometry is CODE, not hand-moved objects:
[`Tools/blender/arch_elements_lib.py`](../../../Tools/blender/arch_elements_lib.py)
holds the primitives, `build_arch_elements.py` builds every element from fixed
seeds, `export_arch_elements.py` writes one FBX per definition id. Rebuild with

```python
exec(open("Tools/blender/build_arch_elements.py").read())
exec(open("Tools/blender/export_arch_elements.py").read())
```

Author there and nowhere else. Blender crashed three times in one session and
took every unsaved hand edit with it; a script made recovery free, and it makes
the art reviewable as a diff. Save the `.blend` right after a rebuild anyway.

### Rules paid for in bugs

- **Recalculate face winding.** `bm.normal_update()` recomputes normals from the
  EXISTING winding; it never fixes winding. Without
  `bmesh.ops.recalc_face_normals` before `to_mesh`, 4842 of 6136 faces shipped
  inside-out (78.9%) — ropes almost entirely — and with double-sided materials
  they render dark and muddy. That is what "the materials broke" looks like.
- **Suspect geometry before colour.** Imported material colour equals the Blender
  palette expressed in gamma (0.720 → 0.865) and renders identically. Measure
  both sides before touching a colour.
- **The FBX axis conversion flips X and Y.** With `axis_forward='-Z'`,
  `axis_up='Y'`, authored `+X` arrives as Unity `−X` and authored `+Y` as `−Z`.
  Never hand-derive a yaw formula from that: pass a direction to
  `Quaternion.LookRotation` and prove it with a measurement. A hand-rolled
  `atan2` drew every floor and roof sector 180° backwards, so a click resolved
  one sector and highlighted the opposite one.
- **Measure in the space the object lives in.** Comparing world positions with
  simulation coordinates without removing the preview root's yaw reported a
  confident, wrong "121°". `root.InverseTransformPoint` first.
- **Exported node names are a runtime contract.** `CampfireSpitMeat` finds the
  spit by the exact name `stick_bar`; a `stick_bar__EXPORT` silently loses every
  meat slot. The exporter parks sources under `__src_` so duplicates can carry
  the authored name without Blender appending `.001`.
- **Root markers stay direct children.** `fire_point` (the flame anchor) is
  looked up with `transform.Find`, which does not recurse. Keep it in
  `ROOT_MARKERS` and re-parent it to the wrapper after instantiating.

### Stage contract

`BuildStage_1/2/3` empties at the root; every direct mesh child of a stage is
ONE delivered resource — sticks, then boards/stones/leaves, then rope. A name
containing `_deco_` is free hardware that ships with its stage. `HL_Door_Pivot`
is a transparent container: its leaf boards count as ordinary stage-2 resources.
`BlueprintArchitectureFactory.ApplyStageProgress` reveals exactly the pieces the
delivered materials paid for and never opens a stage before the previous one is
complete. Keep the bill in `SimBalance` equal to the piece count, and make those
numbers `const`: they are not tuning dials, and a `public static` knob must
otherwise appear in `simdata.json` or `BalanceKnobHygieneGate` goes red.

### Tiling rules for sector art

- Keep a slope INSIDE its hex. A roof panel rises from the outer hex edge to the
  hex centre, so two neighbouring hexes meet along a shared edge at each one's
  LOW point — the same height for any room shape. That is what lets a stretched
  room roof itself with no joiner element, and why a flat roof is not needed.
- Let each sector carry its own radial edge beam plus the outer edge beam. On a
  finished hex the six sectors close the frame with no duplicated beam.
- Clip anything under a sector to the triangle. A bar at constant `y` pokes out
  through the slanted sides, because a sector only exists from `x = |y|/tan30`
  outwards.
- Keep the frame under the deck: verify against the boards' measured underside,
  not by eye.

### Boundary and seam rules

- A wall section is 0.5 wu. Its boards span the FULL section with a small overlap
  and its post pair sits EXACTLY on the seam, or a vertical slot of daylight
  opens between neighbouring sections.
- Horizontal seams between boards are wanted, but as a shiplap step, never as a
  through-gap. Overlap the boards and alternate their thickness.
- A bay authors its post pair on its far (+Z) node only. Where a standalone
  Support element owns that node, drop the bay's posts AND their lashing —
  dropping only the posts leaves rope rings hanging in mid-air.
- Orient the corner support: its pair separates along local `+Z`, so place it
  with `LookRotation(outward)`. Left unrotated it points a random way and the
  corner reads as a missing beam.

### Footprints

- Draw furniture at the CENTROID of its occupied junctions, not at the anchor
  junction. A bed's footprint runs from −0.5625 to +0.9375, so the anchor is
  0.1875 wu off centre and the mesh slid away from its wall.
- A footprint's base axis must match the axis the models are authored on (`+Y`
  at yawStep 0). The wardrobe's row ran at 30° instead and was a full hex step
  (60°) out of sync with the cabinet the player could see.
- The indoor hearth is a single junction. Its authored ring is 0.334 wu against
  a 0.375 lattice, so it never reaches a neighbour; the old seven-junction disc
  was the outdoor campfire's reach and blocked the middle of the hut.

### Constructor overlay

Overlay dots must read THROUGH the building. URP's Unlit shader hardcodes
`ZTest LEqual` and ignores a `_ZTest` float, so use
`HexLive/BlueprintOverlay` (`ZTest Always`, overlay queue) and keep it in
Always Included Shaders or the Player strips it.

## Verify all layers

1. Compare the FBX bounds and pivot reported by the verifier with the intended junction footprint.
2. In the already-open Unity Editor, obey the Unity MCP lease from `AGENTS.md`, wait for import/compilation, and check errors.
3. Inspect the hierarchy: placement root identity except for world position and six-way `Y` yaw; imported FBX correction only below it.
4. Sweep all six yaws. The primary axis must remain on one of the six legal footprint lines and never acquire a `30°` intermediate angle.
5. Check both the authoring fixture and production loading path from the same committed data.
6. Save/reload once. Verify model, footprint, sockets, and interactions still coincide.

If test and production differ, stop and trace which data/factory each loaded. Do not repair the difference with another transform.
