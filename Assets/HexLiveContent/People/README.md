# WebGL people — prepared assets, validation in progress

New content lives here; old people assets remain at their original paths by the
player's latest decision. They are legacy references, excluded from this opt-in
catalog. Do not physically move them. World content is outside this migration.
**No AssetBundles or server publication without the player's separate command.**

## Prepared

- 2 bodies (Marta/Kshishtof), 15 Primal garment fits, 16 female hairstyles with
  two LODs, 3 backpacks fitted to both bodies: **39 prefabs / 55 source FBX**.
- 172 original transform bones and 109 exact morph names/order per body;
  embedded humanoid avatars, IK references, separate male genital renderer.
- All Primal clothing colourways, fur alpha, eight skin IDs on the two bodies.
  Skin IDs currently vary atlas tint; they are not eight independent skin textures.
  Eyes/teeth use separate material slots, eyes currently keep the authored look.
- 25 paint-map assets, generated from the new topology. Skin maps cover all
  7 zones with 128/128 valid samples per zone; eyes/teeth excluded.
- `Preview/PrimalRunning.unity`: local running pair, equipped through BodyBones,
  no server or bundle required. `Validation/Review/` contains outfit/face renders.
- `catalog.json`: 121 records (2 actor, 16 hair, 78 wear, 25 map configs).
  Dry-run and dependency audit passed; old body/wear/hair dependencies excluded.
  Enable future discovery only with `-content-people-catalog primal-v1`.
  Merely importing these files does not switch a live catalog.

## Evidence and limits

`Validation/unity-import.json`: 55/55 imports passed. Marta has 7,192 triangles,
4,410 imported vertices and ~1.65 MiB Editor Mesh memory; Kshishtof body has
15,500 triangles, 8,957 vertices and ~4.61 MiB. These are Editor Mesh measurements,
not total browser RAM. `import-baseline.json` retains the original failed memory
budget before removing split normals and morph normal deltas.

`Validation/wear-run.json`: bindings stayed within 0.00000036 m at rest; 19 run
samples per body stayed finite and moved the feet. Underwear hid male genitals.
`paint-map-coverage.json` and `catalog-dependencies.json` are separate checks.
Source weights are preserved (up to 11/10 influences), not silently capped.

**gameReady remains false.** Real PlayMode dirt/tear/tan pixel checks, gameplay
spawn/portrait/lipsync and browser performance still need validation. Fit and all
16 hair silhouettes need final visual review; automated bounds checks cannot
prove absence of clothing intersections. Icons follow the human wardrobe review.
No browser performance or bundle-loading result is claimed from Editor renders.

## Reproduce without building

Use the official Unity CLI and coordinate UNITY_OWNER with Claude first.
Execution copy: `/Users/shtolyan/hex-girls/webgl-build/HexLive`; source branch:
`claude/webgl-port` at `/Volumes/ORICO/HexLive-webgl`. Return generated assets
and .meta files here; do not commit in the execution copy.

`HexLive.UnityPresentation.Wearing.Editor.PeopleAssetPreparation.PrepareAll`
imports/audits sources, generates prefabs, validates run/fit and bakes maps.
`ValidatePrepared` regenerates maps/catalog/preview after an existing import.
`FinishVisualReview` produces outfit/face images, verifies map coverage and calls
`AtomicContentBatchBuild.AuditPeopleCatalog` (dry run, no BuildPipeline).
`PeoplePaintRuntimeTests` is a graphics PlayMode fixture for local paint assets;
it is not a server/bundle integration test.

The sole Blender source remains
`/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Primal_Wardrobe.blend`.
Source exports and verification tools are in
`Assets/HexLive/UnityPresentation/Wearing/Editor/PeopleSourceTools/`.
No native copies of the FBX Mesh/Avatar subassets belong in this catalog.
