# HexLive — Inventory Icon Generation Spec

How to produce the little 3D pictures the inventory shows for an item, without
opening Unity. Companion to `TOOL_GENERATION_SPEC.md` (that one is about the
**world model**; this one is about the **icon**).

Approved by the icons that shipped: the garment set (`Shorts_10_14636.png` and
friends, originally rendered for the Molly project) and the three
`clothing.shorts_*` icons rendered by this pipeline to match them. Follow this
exactly so every row of the inventory list looks like it came from one camera.

---

## 0. Where an icon lives and how it's found

- File: **`Assets/Resources/HexLive/UI/Items/<itemId>.png`** — 512×512 RGBA,
  transparent background, plus a sprite `.meta`.
- The filename is the item id **verbatim, spaces and capitals included**
  (`FCO Pants Male.png`, `clothing.shorts_red.png`). The id is
  `GarmentDefinition.id` for clothing, `GearConfig.gearId` for tools/items.
- `CharacterPanel.LoadItemIcon` (the inventory panel) loads **only** the exact
  id. `HexInspectorPanel` additionally falls back to `ItemCatalog.Slug(id)`
  (lowercase, non-alphanumerics → `_`). Name the file after the id and both work.
- No icon = the panel falls back to the category emoji. That is the "male
  clothing has no pictures" symptom.

Coverage check (from the repo root):

```bash
comm -23 <(grep -h '^  id: ' Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/*/*.asset | sed 's/^  id: //' | sort) <(ls Assets/Resources/HexLive/UI/Items/*.png | sed 's|.*/||; s|\.png$||' | sort)
```

Same idea for gear: `grep -h '^  gearId: ' Assets/Resources/HexLive/Gear/*.asset`.

## ⭐ 1. Before rendering anything — look for a ready icon

The Molly project (`/Volumes/ORICO/molly_copy`) ships a finished 512×512 icon
next to **every** wear prefab: `Assets/Wear/<Folder>/<Item Id>.png`. The
garment icons already in HexLive are byte-identical copies of those (verified by
md5). All nine male garments came from there — no render, no AI, just a copy.

So: `find /Volumes/ORICO/molly_copy/Assets/Wear -iname '<item>*.png'` first.
Only render when the mesh is HexLive-only (recoloured prints, new props).

## 2. Path A — a garment (source: a Unity `.mesh`)

1. **Find the mesh and the texture.** `Resources/HexLive/Wear/<id>/*.prefab` →
   the `SkinnedMeshRenderer` doc gives `m_Mesh` (guid) and `m_Materials`
   (guids). Resolve a guid to a path by scanning line 2 of the `.meta` files
   under `Assets/ImportedActors`. The `.mat` → `_BaseMap` guid → the albedo jpg
   in `Assets/ImportedActors/Wear/Prints/`.
   *Recolours share one mesh* — the three `clothing.shorts_*` are all
   `Shorts_10_14636/Meshes/Molly.mesh` with a different print.
2. **Mesh → OBJ:**
   ```bash
   python3 Tools/unity_mesh_to_obj.py <Actor>.mesh out.obj <Albedo.jpg>
   ```
   (copy the jpg next to the OBJ; the MTL references it by name). The script
   handles the YAML vertex-stream layout and the left→right-handed flip; its
   docstring has the format notes.
3. **Render** (step 4 below).

## 3. Path B — a tool / prop (source: the shipped model)

The renderer eats a `.glb` directly, so for anything in
`Assets/Resources/HexLive/Objects/` there is no extraction step:

```bash
/Applications/Blender.app/Contents/MacOS/Blender -b -P Tools/render_item_icon.py -- \
    Assets/Resources/HexLive/Objects/tool.bottle.glb /tmp/tool.bottle.png
```

For a prop that only exists as a `LowPolyToolFactory` shape there is no mesh to
render — generate the model first (`TOOL_GENERATION_SPEC.md`), then the icon.

## 4. Render — the fixed camera (do NOT tune casually)

```bash
/Applications/Blender.app/Contents/MacOS/Blender -b -P Tools/render_item_icon.py -- \
    <model.obj|model.glb> <out.png> [--install <itemId>] [--style cloth|tool] \
    [--fit 1.25] [--samples 64]
```

Two presets, because two batches of icons shipped and they are NOT identical.
The script picks by source type — **OBJ ⇒ `cloth`, GLB ⇒ `tool`** — and
`--style` overrides:

| | `cloth` (garments) | `tool` (props/tools) |
|---|---|---|
| matches | `Shorts_10_14636.png` | `tool.axe_stone.png`, `tool.lighter.png` |
| camera dir | `(0.55, −1.0, 0.30)` | `(0.75, −1.0, 0.30)` |
| framing | `ortho_scale = maxdim × 1.25` | `× 1.35` (a touch more air) |
| surface | Roughness 0.62, Specular 0.15, Sheen 0.15, smooth | Roughness 0.9, Specular 0, **flat-shaded** |

Shared by both: ORTHO camera aimed at the bbox centre, Cycles 64 samples +
denoise, `film_transparent`, view transform `Standard`, 512×512 RGBA, and 4
suns — key 4.0 `(0.7,−1,0.9)`, fill 1.8 `(−1,−0.6,0.25)`, rim 2.2
`(−0.2,1,0.6)`, top 1.2 `(0,−0.1,1)`.

Both importers land the model with its front at Blender −Y (OBJ:
`axis_forward='-Z', axis_up='Y'`; glTF: the importer's own Y-up→Z-up), so the
camera math is the same for garments and props.

**Always eyeball the result next to an existing icon** (`Shorts_10_14636.png` is
the reference) before installing — same size in frame, same lighting side.

## 5. Install — the `.meta` by hand

`--install <itemId>` copies the PNG into `Assets/Resources/HexLive/UI/Items/`
and writes a sprite `.meta` cloned from `Bikini Bottom.png.meta` with fresh
`guid:` and `spriteID:` (uuid4). That is the whole Unity side: `textureType: 8`,
`spriteMode: 1`, `alphaIsTransparency: 1` — the file imports as a Sprite on the
next editor launch, no editor session needed.

Never reuse an existing guid, and never let two icons share one.

## 6. Traps

- **A strappy underwear/swim mesh renders "empty"** — bands and straps only, no
  cup/fabric panels. That is the actual game mesh; `panty_leo` looks the same.
  Not a bug, do not "fix" it by inventing geometry.
- **A see-through prop** (the plastic bottle, `alphaMode: BLEND`) would render as
  a ghost over the transparent film — the renderer forces `Alpha = 1` /
  `OPAQUE` for the icon only. The world model keeps its transparency.
- **White on white**: a cream/white item on the pale AI background has no
  silhouette. That matters for the *source image* of an AI prop, not for the
  render (the icon background is transparent).
- Blender here is **3.2.2** → legacy `bpy.ops.import_scene.obj`; the 4.x
  `wm.obj_import` does not exist.
- The renderer must run **from the repo root** when using `--install` (the
  Resources path is relative).

## 7. Still icon-less (as of 2026-08-01)

`item.bandage`, `tool.bottle`, `tool.saw`, `tool.spear` — bottle,
saw and bandage now have GLB models, so Path B covers them in one command each.

---

### Related
`TOOL_GENERATION_SPEC.md` (the world model), `spec.md` §51 (item presentation),
`Tools/unity_mesh_to_obj.py`, `Tools/render_item_icon.py`.
