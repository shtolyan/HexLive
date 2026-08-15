---
name: hexlive-resource-authoring
description: Author a new carryable HexLive resource, item or tool end to end — the simulation id and catalog entry, the flat-shaded Blender model in Resources/HexLive/Objects, its size in ObjectFit, and the inventory icon with its Addressables entry. Use when adding or fixing a board, plank, rope, hide, stone, coconut, bandage, prosthetic part or any tool.* / resource.* / item.* prop; when a dropped item is invisible, tiny, grey, brown-when-it-should-be-light, or has no picture in the inventory list; and when an icon renders blown out to white.
---

# HexLive resource authoring

A carryable thing is **six** artefacts, not one. Ship fewer and the failure is
always silent: the item exists, the code runs, nothing appears.

| # | Artefact | Where | Missing ⇒ |
|---|---|---|---|
| 1 | id | `Simulation/Content/ContentIds.cs` | nothing to reference |
| 2 | catalog entry | `PrototypeContentCatalog.AddPickupItem(...)` | id unknown to the sim |
| 3 | tuning asset (optional) | `Resources/HexLive/WorldObjects/<x>.asset` | code defaults only |
| 4 | **model** | `Resources/HexLive/Objects/<id>.fbx` | grey primitive fallback |
| 5 | **size** | `UnityPresentation/ObjectFit.cs` | 0.6·HexRadius, or a splinter |
| 6 | **icon** | `HexLiveContent/Icons/<id>.png` **+ Addressables entry** | category emoji |

Then re-export `SimData/simdata.json` (Unity menu **HexLive ▸ Export Sim Data
(JSON)**) or every headless test runs on stale data.

## Before modelling: is it really missing?

Check first — a "missing" prop is usually present and unreadable.

```bash
ls Assets/Resources/HexLive/Objects/ | grep <id>
ls Assets/HexLiveContent/Icons/ | grep <id>
grep -n "<id>" Assets/HexLive/UnityPresentation/ObjectFit.cs
grep -n "icon/<id>" Assets/AddressableAssetsData/AssetGroups/HexLive.Icons.asset
```

`WorldPropResources.Load` maps some ids to a `*_native` filename — read
`Environment/WorldPropResources.cs` before concluding the file is absent.

Inspect an existing FBX headlessly rather than guessing (Blender 3.2.2 at
`/Applications/Blender.app/Contents/MacOS/Blender`, `-b` is fine for plain
scripts — the `-b` ban applies only to the MCP add-on):

```python
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=path)
for o in bpy.data.objects:
    print(o.name, o.dimensions, [ms.material.name for ms in o.material_slots])
```

## Model — a script, not a saved .blend

Simple props (a board, a stake, a crate) are cheaper to *state* than to store.
`Tools/make_board_model.py` is the worked example: deterministic seed, one
palette block at the top, one `bmesh` build, one FBX export. Copy it.

Rules that are not style preferences:

- **Take the palette from `Assets/HexLiveContent/source.blend`.** Append the
  real materials (`Sapwood`, `Heartwood`, `Bark`, `StoneGrey`, `Rope`…) with
  `bpy.data.libraries.load` and derive shades from them. A prop whose colour is
  invented drifts out of the set within one asset.
- **Flat shading is the art style.** `use_smooth = False` on every polygon and
  `mesh_smooth_type='FACE'` on export. A smooth-shaded prop reads as plasticine.
- **Chamfer only the real body edges.** Passing every edge to `bmesh.ops.bevel`
  chamfers the interior subdivision grid too: the face fills with ridges and the
  prop renders as a washboard. Filter first:
  `e.calc_face_angle(0.0) > radians(25)`.
- **Do not scale to a target size.** `ObjectFit` normalises ground *and* hand
  from one table. Baking size into a mesh breaks that pairing.
- **Keep the bbox when replacing a model.** Station/furniture assemblies and any
  authored hand pose are built around the old dimensions.
- Export: `axis_forward='-Z'`, `axis_up='Y'`, `object_types={'MESH'}`,
  `path_mode='STRIP'`, `add_leaf_bones=False`, `bake_anim=False`.
- Keep the existing `.fbx.meta` (its GUID) — overwrite the FBX in place.

Budget: the handmade props here are **28–400 triangles**. That is the target,
not 20 000; see `TOOL_GENERATION_SPEC.md` §3c.

## Size — `ObjectFit`, and it decides whether the prop exists

`resource.*` and `tool.*` fall to `0.216 × HexRadius`, the hand-tool size. That
is wrong for anything plank- or log-shaped: at 0.216 a board lies in the grass
three times shorter than the log it was sawn from, and the player reports the
model as missing. Sanity-check every new prop **against its neighbour** — render
both at their real world sizes on a plane before deciding.

Reference: log/stick `0.7`, board `0.5`, boulder `0.45`, rope `0.10`,
food `0.12`, tools `0.216`, lighter `0.072`.

## Icon — the part that fails silently

```bash
/Applications/Blender.app/Contents/MacOS/Blender -b -P Tools/render_item_icon.py -- \
    Assets/Resources/HexLive/Objects/<id>.fbx /tmp/<id>.png \
    [--tilt 22] [--fit 1.10] --install <id>
```

`--install` copies into `Assets/HexLiveContent/Icons/`, writes the sprite
`.meta`, **and registers `icon/<id>` in `HexLive.Icons`**. All three are
required: `Wearing/Garments/ItemIcons` is the only door to item icons and it
asks Addressables and nothing else. A PNG without its group entry imports
cleanly and never appears anywhere in the game.

`Assets/Resources/HexLive/UI/Items` is **dead** — only the migration tooling
still names it. Do not put an icon there.

Two framing traps, both measured:

- **A flat prop burns out.** The shared rig is four suns; a horizontal face gets
  ~2.2× its albedo, so anything above ~0.46 linear clips to white and the form
  disappears. Tilting makes it *worse* (the face turns into the key sun: 29 % →
  52 % clipped). The lever is albedo. Measure instead of eyeballing:

  ```python
  px = [p for p in Image.open(icon).convert("RGBA").getdata() if p[3] > 200]
  clipped = sum(1 for p in px if p[0] >= 253 and p[1] >= 253)
  ```
  Aim for 0 %. A small `--tilt` is still worth it *after* the albedo is right —
  it shows the edge and the end grain instead of a flat rectangle.
- **A thin prop looks tiny at the shared `--fit 1.35`.** Drop to ~1.10 so it
  spans the frame like `resource.log.png`, and compare the two side by side at
  512 px before installing.

⚠️ **Never assign `rotation_euler` on an imported object.** The FBX/glTF
importers keep the axis-conversion rotation there; assigning overwrites it and
the model silently springs back to its original pose. Transform the mesh data:
`ob.data.transform(ob.matrix_world.inverted() @ rot @ ob.matrix_world)`.

## Register and record

- Add the prop to `Tools/world_prop_manifest.json` (`mode:"existing-native"`,
  its `materialSlots`) so `WorldPropBuildGate` guards it at build time.
- Write the decision into `Spec/<N>.md` — the colour, the size and *why*.
  Spec-first is the project rule, and the next agent will otherwise re-tune the
  same two numbers from scratch.
- Re-export `simdata.json` after any tuning-asset change.

## Verify

Render the prop next to its nearest neighbour on a plain ground plane, at the
real `ObjectFit` sizes, and look at that picture before declaring it done.
Colour, size and silhouette are all decided there — none of them are visible in
a diff.
