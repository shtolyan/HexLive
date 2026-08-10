# HexLive — Tool/Weapon Model Generation Spec

How to generate a stone-age tool (axe, knife, hammer, spear, pickaxe…) as a
textured low-poly GLB and wire it into the game so it "just works" with the
existing hand-prop, armed-stance animations, and (testbed) Final-IK strike.

Approved with the stone axe (`tool.axe_stone`). Follow this exactly for every
new tool so they're consistent.

---

## ⭐ KEY PRINCIPLE — read this first

**Generate a HIGH-poly textured mesh, THEN decimate it. Do NOT try to make an AI
produce "low-poly" directly.**

- AI text/image-to-3D models canNOT reliably output clean low-poly game meshes.
  Asking for "low-poly" in a 3D generator gives mush.
- Our look comes from: (a) a **faceted, flat-shaded** source *image* (fal.ai
  flux prompt), (b) a **high-poly** image-to-3D reconstruction (`trellis-2`,
  ~490 K tris — it keeps the facets), (c) **decimation** in Unity
  (UnityMeshSimplifier → ~20-30 K tris), (d) a flat URP/Lit material.
- So the flow is: **flat-shaded IMAGE → high-poly textured GLB → decimate →
  reorient/pivot → prefab.** Never skip straight to a low-poly generator.
- ⚠️ **Decimation is not the last step — see §3c.** It floors at ~20-30 K because
  the AI mesh is a disconnected triangle soup, and 20-30 K is 50-250x the rest of
  the art. Retopologise (or model) after it.

The full step-by-step follows.

---

## 0. Art style (APPROVED — do not deviate)

- **Low-poly, faceted, flat-shaded, cartoon/stylized.** Matches the terrain
  style. NO realism, NO noise/procedural textures, NO smooth organic surfaces.
- Stone-age materials: **chipped gray stone** head/blade, **wooden** handle,
  **brown cord / leather** lashing.
- The **faceting IS the low-poly look** — keep hard edges and flat facets.
- Reference feel: the shipped `tool.axe_stone` (faceted stone head, wooden
  handle, cord wrap).

---

## 1. Source image — fal.ai flux

- Endpoint: `POST https://fal.run/fal-ai/flux/schnell`, header
  `Authorization: Key <fal key>` (key: memory `reference_fal_api_key`).
- Body: `{"prompt":"<PROMPT>","image_size":"square_hd","num_inference_steps":4,"num_images":1}`.
- **Prompt template** (fill `<TOOL>` + the material clause):

  > A stone age `<TOOL>` game prop, low-poly faceted flat-shaded cartoon style,
  > `<a gray chipped stone … lashed with brown cord to a short wooden handle>`,
  > single object centered, three-quarter isometric view, solid soft pale gray
  > background, even soft lighting, no cast shadow, clean crisp silhouette,
  > hard edges, minimalist stylized

- **Image requirements for good image-to-3D:** ONE object, centered, **3/4
  view** (shows volume), **plain pale background**, no cast shadow, crisp
  silhouette. A busy/flat/dark-bg image gives a bad mesh.
- Download the returned `images[0].url`. Eyeball it before spending 3D credits.

## 2. Image → 3D — Artificial Studio, `trellis-2`

- Endpoint: `POST https://api.artificialstudio.ai/api/run`, header
  `Authorization: $ARTIFICIAL_STUDIO_API_KEY` (env, **no** `Bearer`).
- Body: `{"tool":"image-to-3d-object","input":{"model":"trellis-2","image_url":"<fal image url>"}}`.
- **Use `trellis-2`** — it is the faceted, geometry-strong model that matches our
  style. (hunyuan3d-v21 is smoother/off-style; triposr is blobby.) Cost 60 credits.
- Poll `GET /api/generations/{id}` (plural) until `status:success`; `output` = GLB
  URL, `thumbnail` = preview PNG. Download both.

## 3. Fix WebP textures (CRITICAL — trellis-2 always needs this)

trellis-2 GLBs (generator `trimesh`) embed textures as **WebP** and declare
`extensionsRequired:["EXT_texture_webp"]`. glTFast (6.14.x) does NOT support it →
Unity imports the GLB as a bare `DefaultAsset` (no GameObject), console says only
"Failed to import … (see inspector)".

**Fix without Blender** — pure-Python GLB surgery (Pillow with webp support):
parse the JSON+BIN chunks; for each `image` with `mimeType:image/webp` decode its
bufferView bytes → re-encode PNG → append as a new bufferView, set
`mimeType:image/png`; fold each texture's `extensions.EXT_texture_webp.source`
into core `texture.source` and delete the ext; strip `EXT_texture_webp` from
`extensionsRequired/Used`; update `buffers[0].byteLength`; repack with 4-byte
aligned chunks (JSON space-padded, BIN zero-padded). (Working script lived in the
session scratchpad — reuse it.)

## 3b. Headless variant — steps 4-8 without the Unity Editor (bottle / saw / bandage)

When Unity is not running (or you don't want to drive it), do the decimate +
orient + material work in **headless Blender 3.2.2** and ship the result as a
plain `.glb` dropped straight into `Assets/Resources/HexLive/Objects/<id>.glb`
(+ a `.meta` cloned from `tool.machete.glb.meta` with a fresh `guid`). glTFast
imports it on the next editor launch and `Resources.Load<GameObject>` resolves
it exactly like a `.prefab` — no prefab step, no Unity scripting, no MCP.
`tool.machete`, `tool.bottle`, `tool.saw` and `item.bandage` ship this way.

```bash
Blender -b -P decimate_glb.py -- fixed.glb dec.glb 20000     # Decimate/COLLAPSE
Blender -b -P orient_glb.py   -- dec.glb  out.glb <rx ry rz> preview.png [alpha]
```

- **Decimate**: a `DECIMATE`/`COLLAPSE` modifier at `20000/tris` reaches a clean
  20 K (Blender handles the triangle soup better than UnityMeshSimplifier).
- **Shade FLAT before export** (`shade_flat`, `use_auto_smooth = False`): the
  shipped `tool.machete` has 3 verts per tri and the faceting IS the art style.
  A smooth-shaded AI mesh reads as plasticine.
- **Rotation**: Blender is Z-up and the exporter (`export_yup=True`) maps
  Blender **+Z → Unity +Y** and Blender **−Y → Unity +Z**. So "grip along +Y,
  working edge +Z" = stand the long axis along Blender +Z with the teeth/blade
  pointing at Blender −Y. Recenter x/y on the object, `min z → 0`.
  ⚠️ The glTF importer leaves objects in **QUATERNION** rotation mode — assigning
  `rotation_euler` is silently ignored and the mesh exports unrotated. Set
  `rotation_mode = 'XYZ'` first, and check the printed bbox actually changed.
- **Strip the metallicRoughness map** and scale the albedo to 1 K: trellis-2
  bakes a metallic map that renders the saw blade near-black in URP, and 2×2048
  textures make a 7 MB GLB. Albedo only ⇒ ~3 MB and the right colours.
- **Transparency** (the plastic bottle): `blend_method = 'BLEND'` + Principled
  `Alpha` — exports as `alphaMode: BLEND` with a base-colour alpha (0.82), which
  glTFast turns into a transparent URP material. No Unity-side material edit.
- **The in-hand pose goes in the gear asset** (`Resources/HexLive/Gear/<x>.asset`:
  `handPoseAuthored: 1` + `handLocal*`), NOT in `NpcActorView.TryGetHandPropTransform`.
  That baked table sets an ABSOLUTE `localScale`, so it silently breaks the
  moment the model's dimensions change; the asset's scale is a multiplier over
  `ObjectFit`. (`tool.bottle` was exactly this trap — its baked 0.349 was tuned
  for a 0.43-tall procedural bottle.)

## 3c. ⭐ Polygon budget — decimation is NOT the last step

`20000/tris` in §3b and the "~20-30 K" in §5 are not a low-poly budget, they are
**where the decimator gave up**. Measured across the repo:

| | triangles |
|---|---|
| handmade Kenney props (`.fbx`) | **28 - 400** |
| `tool.axe_stone` / `knife` / `pickaxe` / `spear` | 21 310 - 34 830 |
| `tool.machete` / `tool.saw` (as shipped) | 20 000 each |
| `tool.bottle` (voxel-retopologised) | **800** |
| `rope` / `yucca` | 46 855 / 52 760 |

So every AI tool was **50-250x** the rest of the art, and nobody could push it
lower because **a trellis-2 mesh cannot be decimated at all**: it is a triangle
soup — three unshared verts per triangle, ~5 500 disconnected shells, 30 957
boundary edges — so COLLAPSE shrinks triangles into holes instead of merging
them. Welding first gets rid of the holes and leaves spikes. That is the real
reason §5 says it "FLOORS at 30-60 K"; it is not a property of the tool.

**Do not decimate an AI mesh. Replace it.** Two routes, both headless Blender:

- **Volumes** (axe, knife, machete, pickaxe, spear, bottle) —
  `Tools/retopo_tool_glb.py`: voxel-remesh to a clean manifold, decimate THAT,
  re-unwrap, and bake the original albedo onto the new UVs, so the look survives.
  `tool.machete`: 20 000 -> **898 tris**, 3.19 -> 0.36 MB. Choose `voxel` from the
  thinnest feature you must keep — a few voxels across, or it dissolves.
- **Thin plates** (the saw blade) — model it. There COLLAPSE floors for a real
  geometric reason: merging across a shell a few voxels thick would
  self-intersect, so the result is 27 041 faces at ratio 0.005 **and** at 0.001,
  and QuadriFlow refuses the mesh whatever you clean up first (it wants one
  connected manifold; pre-decimating to make it cheap is what breaks that).
  `Tools/make_saw_lowpoly.py` states the saw as a plate, teeth and a grip:
  **220 tris, 17 KB.** Take the flat colours from the old model's albedo
  (k-means over the texture) so the colour does not move.

Either way keep the **bbox and pivot** of the model you replace, or the hand pose
in `Resources/HexLive/Gear/<x>.asset` moves with it. Re-render the inventory icon
afterwards (`Tools/render_item_icon.py --install <itemId>`) — it is a render of
the real mesh, so it is stale the moment the mesh changes.

## 4. Import to Unity

- `import_model_file(source_path=<fixed glb>, name=<StoneX_AI>, output_folder="Assets/_AiGen")`.
  glTFast is installed; the fixed GLB imports as a **GameObject** prefab.
- The import + 2×2048 textures take ~a minute + a domain reload — the MCP bridge
  will time out mid-import; just re-check `manage_asset get_info` until
  `assetType == UnityEngine.GameObject`.

## 5. Decimate

- **UnityMeshSimplifier** (OpenUPM `com.whinarn.unitymeshsimplifier`, scoped
  registry `package.openupm.com`). AI image-to-3D output is a triangle-soup with
  poor connectivity → even with all `Preserve*Edges = false` + `EnableSmartLink`
  it FLOORS at ~30-60K tris (can't reach a few K). Run via `execute_code`;
  `SimplifyMesh` blocks the main thread so the bridge times out but the op
  completes — verify the saved mesh after.
- ⚠️ **That floor is the source mesh, not the simplifier, and 30-60 K is not
  shippable** — see §3c for why and for the two routes that actually reach
  low-poly. This step alone leaves a prop 50-250x heavier than the rest of the art.

## 6. Orient + pivot — CONVENTIONS (must match `tool.axe_stone`)

Target frame (matches the reference hand-made FBX so the GearConfig hand scale
stays consistent):

- **Handle / grip along +Y**, base of the grip at **y = 0** (the pivot sits at
  the handle base).
- **Working edge (blade/head/point) faces +Z (forward).** In-hand +Z is the
  direction the tool "looks" / strikes.
- **Pivot centered on the handle axis** — translate so x,z ≈ 0 pass through the
  grip, NOT the AABB center (the head skews the AABB).
- **Do NOT scale the mesh to any target size.** Final size is normalized at
  runtime by **`ObjectFit`** (one shared table) for BOTH the ground and the hand,
  so a tool is the same physical size everywhere. Tools normalize their max
  dimension to `0.216 × HexRadius`. To resize ALL tools, change that one number in
  `ObjectFit.TargetWorldSize`; never bake size into a mesh or the config.
  The GearConfig `handLocalScale` is a per-item fine MULTIPLIER on top (default 1).

AI meshes come in **diagonal** — an AABB axis-permute does NOT straighten them.
Method that worked:
1. PCA on the **handle-only** verts (bottom ~45 % by height) → `Quaternion.FromToRotation(handleDir, Vector3.up)` to stand it vertical (PCA over ALL verts fails — the flat blade skews the principal axis).
2. Head/blade centroid (top ~30 %) vs handle centroid (bottom) in XZ → yaw so the blade points **+Z**; flip 180° if it landed on −Z.
3. Recenter: handle-axis centroid → x,z = 0; `min.y → 0`. (No size scaling — `ObjectFit` normalizes the final size in-game.)
4. Bake it all into the mesh vertices+normals (so no runtime transform needed).

## 7. Material

- Fresh `Universal Render Pipeline/Lit`; `_BaseMap` = the extracted albedo PNG;
  `_Smoothness = 0.1` (like `LowPolyToolFactory.FlatMat`). Extract the albedo from
  the imported glTF material via blit→`ReadPixels`→`EncodeToPNG`→import as a PNG
  asset (so the prefab doesn't depend on the deleted intermediate GLBs).

## 8. Prefab + integration

- Save the prefab to **`Assets/Resources/HexLive/Objects/<definitionId>.prefab`**
  (`PrefabUtility.SaveAsPrefabAsset` of a GO with MeshFilter(oriented mesh) +
  MeshRenderer(material)). Both the **hand** (`NpcActorView.SetHandProp`) and the
  **ground** (`HexWorldRenderer.CreateObjectView`) load `Resources.Load<GameObject>
  ("HexLive/Objects/<id>")` FIRST — so **no code change is needed**; they pick it up.
- If a hand-made `.fbx` already sits at that id, **rename it to a `__backup` name**
  (`AssetDatabase.RenameAsset` preserves the GUID → no broken refs) so
  `Resources.Load` resolves to the new `.prefab`.
- Author the **hand pose** in the tool's `GearConfig` asset (раздел «Хват в руке»; AxeChopTest → Save)
  for the id: `localPosition / localEuler / localScale` in the acting hand's local
  space. Tune it live in the `AxeChopTest` scene (drag the prop / edit the fields →
  Save), then it's used by the game's `SetHandProp`.

## 9. What "just works" once wired (no extra code per tool)

- Any **`tool.*`** id in hand → **armed idle** (Standing Idle) + **armed walk**
  (Standing Walk Forward) automatically (`NpcActorView.UpdateArmedStance`,
  `NpcAnimSet.armedIdle/armedWalk`).
- Placed in the hand via the `ItemAttachConfig` pose; visible on the ground via the
  same prefab.
- **Axe / pickaxe** → the looping **Chop** clip on `Harvest` / `Process`; other
  tools → the crouch `Work` pose (see `NpcActorView.ActionFromInteraction`).
- Final-IK **strike-to-point** (hand reaches + body leans + LookAt at the target)
  is currently in the **`AxeChopTest`** testbed only (`AxeChopTestBootstrap`); it
  will be ported into gameplay and must run ALONGSIDE the `NpcActorView` gaze
  LookAt (never zero LookAt).

## 10. Cleanup

- Delete intermediate/duplicate GLBs (timed-out re-imports pile up 27 MB each).
- Keep: the oriented mesh `.asset`, the albedo `.png`, the material `.mat`, and the
  `Resources/HexLive/Objects/<id>.prefab`.

---

### Related memory
`reference_ai_image_to_3d`, `reference_npc_action_clip_states`,
`project_axe_chop_ik_testbed`, `reference_ai_animation_pipeline`.
