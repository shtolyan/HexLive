---
name: hexlive-furniture-authoring
description: Author, export, integrate, or repair HexLive furniture shared by Blender and Unity. Use for furniture models or FBXs; pivot, scale, axis, orientation, 60-degree rotation, hex/junction footprint, wall alignment, construction stages, Blender-to-Unity import, HutLayoutDesigner, test-versus-production placement mismatches, or assets such as beds, wardrobes, hearths, racks, collectors, and workbenches.
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

## Verify all layers

1. Compare the FBX bounds and pivot reported by the verifier with the intended junction footprint.
2. In the already-open Unity Editor, obey the Unity MCP lease from `AGENTS.md`, wait for import/compilation, and check errors.
3. Inspect the hierarchy: placement root identity except for world position and six-way `Y` yaw; imported FBX correction only below it.
4. Sweep all six yaws. The primary axis must remain on one of the six legal footprint lines and never acquire a `30°` intermediate angle.
5. Check both the authoring fixture and production loading path from the same committed data.
6. Save/reload once. Verify model, footprint, sockets, and interactions still coincide.

If test and production differ, stop and trace which data/factory each loaded. Do not repair the difference with another transform.
