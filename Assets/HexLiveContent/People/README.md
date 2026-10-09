# WebGL people — prepared assets, validation in progress

New content lives here; old people assets remain at their original paths by the
player's latest decision. They are legacy references, excluded from this opt-in
catalog. Do not physically move them. World content is outside this migration.
**2026-10-09: the player authorized the local WebGL AssetBundle build.**
Singapore publication and changes to its active registry remain separate.
The earlier preparation reports retain their historical authorization flags;
the actual build receipt records the newly authorized local build.

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
  All 78 wear simulation blocks passed the server metadata validator locally.
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

`Validation/paint-playmode.xml`: graphics PlayMode passed 3/3 tests. All 78
wear variants changed dirt pixels and generated additional tear holes; drying
restored material values, removal restored the original skeleton count. Both
bodies passed skin-tone/blood pixel checks with eyes/teeth unchanged. Selected
skin tint survived weathering updates. These tests load the new assets directly;
they do not validate remote bundle loading.

**gameReady remains false.** Gameplay spawn/portrait/lipsync through the live
catalog and browser performance still need validation. Fit and all
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
`FinalizePreparedData` audits the catalog/maps and removes obsolete native
Mesh/Avatar copies only when the catalog has no dependencies on them.
`PeoplePaintRuntimeTests` is a graphics PlayMode fixture for local paint assets;
it is not a server/bundle integration test.

The sole Blender source remains
`/Volumes/ORICO/HexLive/_ArtSource/Characters/BrowserLowPoly/Primal_Wardrobe.blend`.
Source exports and verification tools are in
`Assets/HexLive/UnityPresentation/Wearing/Editor/PeopleSourceTools/`.
No native copies of the FBX Mesh/Avatar subassets belong in this catalog.

## Server handoff for the future build command

A platform-specific client index alone does not isolate the simulated wardrobe.
`AssetGarmentCatalog.Materialize` uses every active wear record in its registry,
including desktop-only items. Adding these records to the old Singapore registry
would therefore leave old clothes spawnable but invisible to the WebGL client.
Prepare a separate WebGL registry/content root, or obtain an explicit migration
plan for the existing active wardrobe before publication. Preserve world content
and do not retire desktop records as a side effect of this preparation.

The 78 new wear records include `metadata.simulation` for the server's real
validator; flat visual metadata is insufficient for new male/backpack IDs.
`Validation/catalog-simulation.json` records its local result. Existing saved
legacy clothing needs a declared migration/fallback when choosing the new root.
No registry, server binary, simdata, service or live catalog was changed here.

## Authorized local WebGL build

`Tools/build_webgl_content.py build` uses Unity CLI after ownership handoff,
runs the full dependency/prefab/material preflight, builds primal-v1 plus world
content, and packages candidates with relative payload paths and SHA-256 checks.
The output excludes legacy clothes, bodies, hair, eye maps and helmet source art.
`verify --package <directory>` checks a transferred copy without Unity or a server.
It does not publish, change a server, or turn a preparation report into visual acceptance.

### Локальная сборка WebGL — 2026-10-09

Готов переносимый пакет `webgl-primal-v1-ready.tar.gz`: **230 записей**,
266 130 479 байт payload; архив 241 663 324 байт. Состав: 2 тела, 16 причёсок,
78 вариантов одежды/рюкзаков, 7 построек, 57 объектов, 2 животных, 8 протезов,
15 VFX и 45 конфигураций (включая simdata). Сборка на `b0804c8cc8cca2f76ebe39bd589bb106e5586548`.
Полный исходный прогон: 250/250, ошибок 0; 21 неиспользуемая legacy normal-карта
исключена из пакета после проверки всех зависимостей.

Проверки: 10/10 simulation, 3/3 graphics PlayMode, 7/7 упаковщик; повторно
открыты все 121 People payload, 5 удерживались одновременно. Проверены
морфы, Avatar/controller, кости, материалы, слой и covers. Изменение места
пакета прошло проверку SHA-256. Локальный сервер принял 230/230 записей,
HTTP index вернул 230 и platformMissing=0; 6 скачанных HTTP payload совпали
по SHA-256. Архив повторно прочитан и проверены все 230 payload.

SHA-256 архива: `e5b3b0388078c3a7af3c9508461fccc8180f5f6fff6c0e53654fc24fc90e6304`.
Архив и `.sha256` находятся в `/Users/shtolyan/hex-girls/webgl-build/packages/`.
Внутри пакета — inventory, manifest, исходный build summary и отчёты
`people-payload-validation.json` / `local-registry-validation.json`.

На production ничего не опубликовано. `gameReady=false` сохраняет ограничение:
полный браузерный рендер/производительность ещё предстоит проверить с плеером Claude.
Не смешивать новый гардероб с активной legacy выдачей старого server registry;
для теста использован отдельный локальный root только с WebGL-контентом.
