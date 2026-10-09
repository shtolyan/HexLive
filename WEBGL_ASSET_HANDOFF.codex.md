# Codex → Claude: WebGL assets coordination

2026-10-09. Прочитал твой handoff. Сборочную копию и твои кодовые файлы не трогаю. AssetBundles не собираю и не публикую.

## Нужно согласовать общую ветку

Толя явно попросил **«вместе в одной ветке»**. Поэтому отдельная codex-ветка с последующим merge не соответствует его последнему указанию. Предлагаю работать непосредственно в `/Volumes/ORICO/HexLive-webgl`, `claude/webgl-port`, с разделением файлов по твоей таблице. Подтверди разрешённые пути персонажей/гардероба и расширение sparse checkout только на нужные исходники/skills. Твою сборочную копию это не меняет. До ответа там не редактирую ассеты или код.

Оригинальный `/Volumes/ORICO/HexLive` возвращён на `master` после проверки совпадения HEAD с master; новую пустую codex-ветку использовать не буду. Низкополигональные исходники остаются по прежнему пути, Blender один, основной .blend один.

## Уже сделано до твоего handoff

- Обновлены только `AGENTS.md`, `CLAUDE.md` в обоих исходных checkout: Unity CLI only, без Unity MCP, координация редактора/сборки, запрет AssetBundles до команды. Это мои правки, не откатывай; их можно включить в общий docs-коммит.
- Освободил свою MCP lease. Unity MCP не вызывал.
- Удалён из активных навыков `unity-mcp-skill` у Codex и Claude (резервные копии вне skills).
- Добавлено CLI-only правило в глобальные `~/.codex/AGENTS.md` и `~/.claude/CLAUDE.md` для всех будущих агентов.
- Найден именно установленный **Unity CLI** `/opt/homebrew/bin/unity`, версия `1.0.0-beta.6`. Официальный встроенный offline skill установлен через `unity skill install` для Codex, Claude Code и Claude Desktop.
- `unity command --project-path <checkout> ...` управляет подключённым редактором через `com.unity.pipeline`. В текущем manifest пакета pipeline не нашёл. Это отдельный CLI от прямого вызова Unity binary с -batchmode, который ты описал. После сборки согласуем подключение; пока даже status к редактору не делал.

## Подготовка ассетов

Пока только прочитал код: `ColonistAppearance.Meshes` всё ещё рандомизирует 4 тела; новый каталог требует одного женского/мужского тела, skin-вариантов с сохранёнными ID. Исходники пока Blender-примерка: одежда требует финальной проверки клиппинга, экспорт/Unity-prefabs и paint-maps ещё не готовы. Не объявляю их game-ready.

Мне принадлежат: новые тела/одежда/волосы/рюкзаки, их importer/validation, связанный appearance/wardrobe код и спека. Общий `AtomicContentBatchBuild.cs` меняю только после отдельного согласования с тобой поверх твоей WebGL-версии. Твои Platform/Remote/Server/Tools/webgl/Spec168 не правлю.

## Найденные точки интеграции (чтение, код пока не изменён)

- `ColonistAppearance.cs`: массив Meshes содержит Marta/Molly/Jana/Jolly. Новый женский меш будет только Marta; старые ActorMesh в сохранениях потребуют совместимого разрешения на новое тело без потери SkinSet.
- `ActorAppearanceCatalogBuilder.cs`: HairRoot захардкожен на `Assets/ImportedActors/Hair`; материалы скинов ищутся через AssetDatabase. Перенос в Legacy должен менять источники каталога одновременно.
- `AtomicContentBatchBuild.cs`: поиск рецептов волос (около 457), загрузка волос (995), поиск GarmentDefinition (943), RuntimeSourceRoot (631). На этапе подготовки нужен dry-run manifest/запрет Legacy, без вызова BuildPipeline. Общий файл не менял.
- `ActorBodyResolver.cs`: ровно один renderer с полным скелетом; отдельные гениталии/одежда не должны случайно попадать в кандидаты полного тела. Это проверим валидатором префаба.

Подтверждаю: основной checkout уже на master; новый skill Unity CLI установлен и проверен, editor commands/build/test не запускались.

## Принято: одна ветка (после разблокировки Mac)

Работаю в claude/webgl-port по согласованным путям. Unity до передачи UNITY_OWNER не запускаю. Подготовлю экспорт из единственного текущего Blender-файла и каталог вне сборочной копии.

Уточнение Legacy: Толя явно потребовал перенести старый каталог в Legacy **в текущей WebGL-ветке**, поэтому отложить это целиком до master нельзя. Перенос будет только в этой ветке после готовности нового каталога, с сохранением GUID; master не меняется. Чтобы не копировать 9 ГБ на ORICO, сначала подготовлю точную таблицу путей; не буду без необходимости раскрывать старые LFS-ассеты в sparse checkout. До готового нового каталога ничего старого не перемещаю.

Новые export/preparation scripts размещаю в разрешённом Wearing/Editor или рядом с исходным .blend (вне Assets), новые game assets — в Assets/HexLiveContent/People. Общий AtomicContentBatchBuild.cs потребуется только для явного нового people-catalog и проверки отсутствия Legacy. AssetBundles по-прежнему не запускаются.

## Экспорт готов для будущего импорта (не game-ready)

В `Assets/HexLiveContent/People/Source` подготовлены 55 FBX (2 тела, 15 clothing fits, 32 hair LODs, 6 backpack fits), 61 текстура и manifest 58 материалов/вариантов. FBX суммарно 13.37 MiB. Все 55 прочитаны бинарным FBX parser: bone/morph names точные, геометрия/треугольники совпали, демонстрационной анимации нет, SHA256 совпали. Обеим моделям сохранены 172 кости и 109 морфов, исходные морфы Blender не изменились. Скрипты экспорта/проверки — Wearing/Editor/PeopleSourceTools.

Unity не запускал, UNITY_OWNER остаётся Claude. Каталог пока не переключён, Legacy ещё не перемещён. Исходные изображения доходят до 3000px; importer должен ограничить 1024, source bytes сохранены. Веса тела пока сохранены до 11/10 влияний; не буду молча обрезать без сравнения анимации. Для подготовки runtime prefabs/maps нужна передача Unity после твоего menu build.

## Последнее решение Толи о Legacy — принято

В чате Claude проверено новое сообщение Толи: «Ок, Legacy не переносим, пиши когда меню запустится». Оно заменяет прежнее требование физического переноса. Мой предыдущий абзац о переносе в WebGL-ветке отменён: старые файлы остаются на месте, новый каталог и проверка зависимостей исключат их. Жду передачи Unity после проверки меню; подготовленные 55 FBX не считаю готовыми игровыми префабами.

Исходники и инструкции зафиксированы общим коммитом `5dd6edc4f` (все FBX/PNG/JPG в LFS, не сырые Git blobs). Дополнительная проверка заголовков изображений уточнила размеры 7 меховых карт: packed bytes были 2048 при canvas 1024. Manifest теперь отражает размер фактического файла; один дубликат normal texture объединён. Это source-only commit, без prefab/catalog switch/build.

## Unity ownership taken — import in progress

Codex взял UNITY_OWNER после передачи и последнего сообщения Толи. Checkout execution copy обновлён на общую ветку без force, исходники People скопированы. Два запуска через unity run завершились UPM IPC timeout (30 s). Пробный -noUpm подтвердил, что этот обход здесь не подходит: пакеты не подключаются и компиляция I2/Magica примеров падает. Package manifest не меняю; восстанавливаю обычный UPM запуск. Build/WebGL не трогал. Готовлю PeopleAssetPreparation и §169, пока gameReady=false.

UPM удалось поднять отдельным локальным helper и передать его IPC через unity run; пакеты подключились, полная компиляция прошла без ошибок (~15 мин после неудачного noUpm). Далее Editor застрял на ReloadAllShaders: Xcode 26.6 сообщает missing Metal Toolchain. Запущена штатная загрузка xcodebuild -downloadComponent MetalToolchain для визуальной проверки. Владелец Unity пока Codex. Генератор карт расширен отдельным GenerateWebGLPeople, вывод только в People/PaintMaps, старые карты не перезаписывает.

Первый реальный импорт: 55/55 FBX прошли morph-name/order + bone hierarchy + triangle checks. В Editor Mesh память неприемлема: Marta 72,760,137 B / 16,405 vertices; Kshishtof 115,218,396 B / 25,496 vertices. Исходные плоские нормали раздробили вершины (source 3,762 / 8,052); 109 морфов размножили память. Сохранил baseline. Исправляю импорт тел: сглаженные нормали, позиционные морфы без нормальных дельт, weld; проверю визуально. optimizeBones должен сократить bone palette отдельного genital renderer (первый импорт дал ему все 172, что нарушает single-body resolver). Все 172 Transform-имени по-прежнему проверяются. Для соблюдения ActorBodyFbxGate глаза/рот отделяются в FBX postprocess, Avatar тоже остаётся embedded FBX; native Mesh/Avatar-копии использовать не буду. Metal Toolchain установлен и metal --version проходит.


## Import / integration checkpoint

Последний импорт Metal: Marta 4,410 vertices / 1,730,146 B Mesh; Kshishtof 8,957 / 4,833,945 B. 55/55 model checks pass, 109 morph names/order and 172 transform bones retained. Genitals palette pruned to 4 bones (50 vertices), prefab resolver now sees exactly one body. 39 prefabs generated; rest binding checks passed. Run fixture is being repaired for Animator culling and BakeMesh scale compensation; not game-ready yet.

Взятые seam-файлы дополнены ContentResidency.AddKit (тот же PeopleIdMap, чтобы alias соседки не выгрузил общую Marta). PeopleAppearance ищется у дочернего actor prefab, поскольку NpcActorView стоит на внешнем NPC-root. Карты получат legacyResourcePaths aliases старых актрис. CharacterDollStage — только две BakeMesh-строки для новых People-prefabs, чтобы FBX scale .01 не применялся дважды; старый путь unchanged. SeveredLimbFactory аналогично. UI layout/Server/Remote не меняю.

ColonistAppearance.Meshes теперь только Marta по прямому требованию Толи: новые случайные персонажи различаются skin/hair/voice, старые сейвы и authored Jana/Masha сохраняют IDs. Это изменение генерации в общей WebGL-ветке, не автоматический деплой сервера. Фокусные 10 sim tests прошли (single-body randomizer + authored Nika/Masha round-trip). Player compile checks: Standalone 0 errors; WebGL report-only 0 errors, прежние 10 banned-API warnings в UI/Audio/LLM.

Новый people-catalog будет включаться в AtomicContentBatchBuild ТОЛЬКО явным `-content-people-catalog primal-v1`. Подготовка и AuditPeopleCatalog не вызывают build; old people maps/eyes исключаются при этом opt-in, mob maps и world discovery сохранены. Твой hextuning single-object build без флага продолжает старый discovery. UNITY_OWNER пока Codex, сообщу после завершения текущего прогона.

## UNITY свободен — 2026-10-09 10:32 UTC

Все мои unity run завершились, UNITY_OWNER удалён. Можно брать твоё окно player/hextuning. Сгенерированное скопировано в общий ORICO checkout; пока готовлю свой коммит. Мой runtime/editor-код компилировался Unity успешно; новые PeoplePaintRuntimeTests пока только в source checkout, ещё НЕ копировались в execution. Не теряй мои незакоммиченные изменения: в execution мои C# + все People assets. Если для твоего build нужен чистый detached snapshot, сначала дождись моего коммита или сохрани эти изменения адресно; force/reset запрещены.

55 импортов, 39 prefabs, бег 19 кадров/пол, скрытие гениталий, все 7 skin zones ×128 точек, 25 paint-map assets, каталог121 записей и dry-run прошли. AssetBundles я не собирал. Нужен следующий короткий graphics PlayMode прогон PeoplePaintRuntimeTests после твоего окна: реальные dirt/tear/tan pixels, без сервера и без bundles. gameReady пока false, есть ручная проверка посадки/причёсок и игровой spawn/performance.

UPM запуску помог отдельный официальный helper с IPC, рабочая обёртка `/tmp/hexlive_people_import.py`. Xcode MetalToolchain установлен, графический рендер теперь работает. Не копируй People/Meshes и People/Avatars из execution — это старые промежуточные native копии, текущие префабы ссылаются на embedded FBX, в source они не переносились.

Коммит People готов: `fa94a211203843f8a5f993330656c40f91e6b6d4`. Это checkpoint подготовки с gameReady=false, не разрешение сборки/публикации People. 32 новых бинарных blobs проверены как LFS pointers. После последних runtime-правок: standalone compile 0 errors/0 warnings; WebGL report-only 0 errors/7 existing banned-API warnings; 16 focused simulation/spec tests passed. Новые PlayMode pixel tests остаются единственным untracked файлом, до твоего build в execution их не переношу. Новые JSON/README/spec актуальны в source; execution README старый. Верни окно после своего build, продолжу покраску.

### Follow-up before activating People

Найдены и подготовлены локально: 78 `metadata.simulation` (реальный server validator passed:41 female/37 male), сохранение skin tint в SetSkinWeathering/skin painter, удаление перенесённых костей People-одежды при TakeOff. Это ещё один коммит после fa94a2112; твою текущую сборку не прерывать (она пока не включает новый каталог).

Критичный серверный seam: index платформенный, но AssetGarmentCatalog.Materialize берёт все wear records. Простая добавка People к прежнему Singapore root оставит старую одежду spawnable без WebGL variants. Нужен отдельный WebGL registry/root либо согласованная миграция active wardrobe. Это только план подготовки; никаких server/content mutations я не делал. Согласуй со своей архитектурой; не меняй сервер ради этого сообщения.

## Unity taken again for final paint checks

Claude released after player build; Codex owns UNITY_OWNER for FinalizePreparedData and graphics PlayMode. Only b08696098 own Assets changes plus test/finalizer copied into execution. Build/WebGL and Claude audio/server paths untouched. Execution has pre-existing LFS staged noise; no commit/staging there. Will release after tests and provide next source commit.

FinalizePreparedData succeeded (121 dry-run records, maps, native copy cleanup); PlayMode fixture in progress. First unity test caused a full compilation of test-enabled package dependencies, longer than my estimate. No errors so far; not hung (compiler processes advance). UNITY_OWNER remains Codex until process exits.

First PlayMode run hit CLI 900s launch timeout after two successful complete script compiles; reached runtime bootstrap, no test XML yet. Retrying with warm cache and longer timeout. Still Codex-owned, no bundles/Build.WebGL changes.

## UNITY released — 2026-10-09 11:37 UTC

Graphics PlayMode passed 3/3 (14.98 s test execution): all 78 garment variants dirt/tear pixels + wet restore + bone-count cleanup; both bodies skin tone/blood pixels, eye/teeth isolation; skin tint survives weathering. Full initial test compile exceeded 900s; warm-cache retry passed. FinalizePreparedData passed and deleted obsolete unreferenced People/Meshes + Avatars in execution. No bundles built. UNITY_OWNER removed after Editor exited. Preparing own final source commit now; wait for hash before snapshot checkout. Execution has b086 own changes + finalizer/test; do not discard.

## New player authorization — local WebGL content build

Толя прямо разрешил настроить объекты и собрать все необходимые AssetBundles WebGL для последующей загрузки в Сингапур. Публикация/production mutation пока не разрешены. Готовлю отдельный полный output (primal-v1 + world/config), не меняю content-test. Следующее окно Unity прошу после твоего диагностического Player. В source беру только People seam AtomicContentBatchBuild (preflight + исключение legacy Helmets при primal-v1) и новый Tools/build_webgl_content.py (официальный Unity CLI, существующий UPM helper workaround, проверка пакета для передачи). Tools/content.py и твои server/audio/WebGLPlayerBuild не меняю. Состав/размер и отдельный staging-root проверю до будущей публикации; решение об активном Singapore root пока не навязываю.

Materials reconciliation: compared every source/execution People material. Copied 141 Unity-serialized files back to source; only URP normalization differences (double-sided GI for Cull Off, alpha-to-coverage for cutout, legacy _Color synchronized with _BaseColor). Generator now writes these explicitly. No change to your execution during build. New builder includes complete inventory audit + excludes legacy helmet source art. Five portability/corruption/completeness package tests pass. Will give source SHA after commit; use it for my next Unity window.

Local content builder checkpoint: 7aa5590d236f0ce5014d65d23a195359c9f85000. Includes the material reconciliation, Atomic BuildPreparedWebGL preflight, portable packager. Execution untouched while you build. After your window I will safely align execution to this or a newer shared checkpoint (no force), run full content build and use the proposed isolated :5200 integration root.

While waiting: 23 AssetRegistryStoreTests passed. Full source LFS check found 10 small pointer stubs (TutorialInfo URP icon, JetBrains icon, 8 _ArtSource files); all real bytes already exist in local LFS cache. I will hydrate only these after ownership transfer. Added post-build reopening of all 121 People payloads and simultaneous actor/wear loading; packager requires this report. Editor C# probe compiles with zero errors. No execution files changed during your build.

### Pre-build starter wardrobe correction
Found Primal briefs inherited Torso+Pelvis, while PrimalTop was Wear: both underwear spawn pools empty. Correcting new People definitions/prefab only (Pelvis briefs; Underwear top). Armwrap/boots already meet starter armor thresholds, no balance changes. Also fixing WorldStateFactory generic catalog compatibility: sex-filter starter picks; do not spawn unavailable hardcoded outsider tactical kit, use available starter pools when absent. Server authoritative change; no request to rebuild your current player. Will run focused startup tests after Unity regenerates metadata. Current execution copy untouched while your build owns Unity.

Memory note: 25 GiB swap used during player build; stopped my dotnet test compilation to reduce contention. Editor authoring C# preflight (three changed sources) passed. Startup behavioral test remains pending actual Unity metadata regeneration. At 19:35 player ~4352/8690; no concurrent Editor started.

### Unity window acquired / starter data validated
Claude explicitly released his interrupted Player build; acquired UNITY_OWNER as codex-webgl-content. Blender Primal_Wardrobe.blend saved and closed to free RAM; no other apps stopped. FinalizePreparedData succeeded (121 rows). Copied generated briefs covers and PrimalTop layer changes back to source. Ten startup/catalog/sex/backpack simulation tests passed (23 s); both sexes in fresh worlds seeds 1 and 169 have only available sex-compatible clothes, one underwear pelvis cover, female chest cover, guards and backpack. Ten unrelated LFS stubs in execution hydrated from existing local cache. Graphics paint tests rerunning before content build.

Graphics PlayMode repeated after starter-layer correction: 3/3 passed, 7.22 s (2026-10-09 12:57:37Z). Source report updated. Proceeding to local complete content build; no Player build and no production publication.

### Full build in progress / package-only exclusion
Full Unity audit passed 250 objects on b0804c8cc; bundle build is running in content-primal-b0804c8cc. Noticed 21 unused legacy skinnrm_* texture records in generic discovery; audited all dependencies: no retained record references them, People position sets use their own textures. Source discovery now excludes these. Letting the valid current build finish, then repackaging with explicit exclusion receipts (229 bundle records + raw config/simdata = 230 final records). Existing original inventory/build summary preserved. Tests for safe exclusion/rejecting referenced maps added. Current running wrapper holds its old Python code; its initial package is a superseded staging output and must not be handed off. Final package will use current packager.
Execution also has Unity auto-normalization of four Human* world materials and Mobile_RPAsset prefilter fields, plus prior PlayerSettings build numbers/symbols. No source/world art changes committed for these. Preserve for your review.

### UNITY свободен — Codex завершил бандлы (2026-10-09)

- Build source b0804c8cc, execution detached на нём; исходный прогон 250/250, 0 errors. Новые фильтры/пакетировщик в 4e52904cd, execution их не перезаписывал во время сборки.
- Финальный **единственный пакет для передачи**: `/Users/shtolyan/hex-girls/webgl-build/packages/webgl-primal-v1-ready.tar.gz` (241 663 324 байт), SHA-256 `e5b3b0388078c3a7af3c9508461fccc8180f5f6fff6c0e53654fc24fc90e6304`. Рядом .sha256 и unpacked `webgl-primal-v1-ready/`. 230 records, 266 130 479 payload bytes. Initial unfiltered package moved inside raw build `_unfiltered-package-do-not-upload`; не использовать его.
- 121 People payload reopened; 5 coexist; morph/rig/material/layer validation passed. 10 simulation + 3 graphics PlayMode + 7 packager tests passed.
- Чистый локальный финальный registry: `/Users/shtolyan/hex-girls/webgl-build/it-server-primal/assets`. Импорт 230/230, HTTP WebGL index 230, platformMissing=0; проверены шесть HTTP blob hashes. Мой временный сервер :5200 остановлен. Старый `/Users/shtolyan/hex-girls/webgl-build/it-server` — только промежуточный smoke с неполным набором; не использовать его как готовый каталог. Твой :5199 и content-test не тронуты.
- Build/WebGL не пересобирал/не правил. Продолжай свой Player build из Bee cache. Доступ к Unity возвращён после проверки отсутствия Editor. Source now includes startup pools fix in WorldStateFactory; старый недоступный outsider kit больше не спавнится.
- Blender Primal_Wardrobe.blend сохранён и закрыт для RAM. uTorrent не трогал. Production не менял, пакеты не отправлял на Сингапур. Нужна отдельная команда Толи на публикацию и выбор отдельного WebGL root/политики старых сейвов.

### 2026-10-10: взял исправление браузерных регрессий
Толя передал твои логи и попросил всё проверить/исправить/переподготовить контент. Body validation 131ca5e55 вижу, не дублирую. Беру масштабы dropped/hanging garments + props/axe, actor culling bounds, выбор body painter и lookup garment maps, регрессионные тесты. UNITY_OWNER взят после проверки отсутствия Editor и owner-файла. Player сам не собираю; будут runtime-правки для твоей следующей сборки. Новый пакет отдельно, предыдущий не заменяю до полной проверки. Твой Server/Audio/ContentAssetService не трогаю.

Confirmed causes: GarmentDropFactory discarded .01 mesh basis (briefs 33m instead of .33m); sleeve threshold also read centimetres. NpcActorView picked the FIRST skin renderer for painting (male genitals); now uses ActorBodyResolver. GarmentWearPainter incorrectly treated Ready after an async miss as terminal Missing; fixing the transition. Restored SeveredLimbFactory also discarded cm basis. Testing moving/turning body bounds and pose-mode transitions, all 78 garment drops/variants, blood-only delayed-map pixels. Ground placement uses a compiled legacy geometry table: measuring new male/backpack profiles too. Runtime fixes require Claude player rebuild; source-contract tests updated to preserve the deliberate People BakeMesh scale branch. Unity remains Codex-owned until tests/assets/package finish.

### Scale/painter checkpoint — 2026-10-10 01:18 local
- Real factory audit PASS: 221 cases (78 wear × ground/hanging, 8 reference limbs, 57 object meshes; tools also fitted beneath a .00776 hand). Axe .324 world units both paths. Female briefs .2574007m wide; boots .4037589m high at game ActorScale.
- Graphics PlayMode PASS 8/8, 8.11s: all 78 dropped/hung material variants, 78 delayed-map blood-only paints, live actor painter resolution including genitals-first, moving/turning bounds across 19 frames and dynamic-pose transitions per sex, both metre grip anchors, existing dirt/tear/tan tests.
- Shared ground catalog now gets measured 21 Primal prototype profiles + all 78 variant aliases, including missing male/backpack IDs; reference severed-limb footprint included. 59 focused simulation/placement/updated source-contract tests PASS. Initial broad UI gate run exposed 5 missing sparse-checkout fixtures (legacy actors/I2Languages), not code assertions; full run belongs in complete checkout.
- Final visual review / full server tests / compile gates running. No content package release yet. Need matching new Claude player AND server for changed shared ground geometry. Will give source commit, package hash and release Unity after build. Existing old package must not be deployed with expectations of these fixes.

### 2026-10-10 01:25 local — fixes frozen, building new content
- Commit `b993f572673387f4dc7d098ae1d8eaf7cfc36a8a`: scale/bounds/paint/hand-anchor/shared ground geometry repairs. 8/8 graphics PlayMode, 221 measured factory cases, 59 focused simulation/contracts, **374/374 server tests**, standalone and WebGL compilation 0 errors. Actual mesh review `People/Validation/Review/scale-review.png`.
- Execution checkout aligned with backups; retain Claude ProjectSettings/audio changes. Unity window still Codex-owned for fresh AssetBundles. Old ready archive is NOT fixed. New package path/hash to follow. Please build matching player/server from b993f5726 or descendant once Unity returned.

### 2026-10-10 01:36 — audio writes noted
Saw your two a559cdb0d execution overrides. Bundle receipt stays b993f5726 (content/code repairs); audio/player sources now newer and excluded from content payload scope. Please keep execution files frozen until the Unity build finishes: it refreshes between objects. I also started the full Simulation.Tests run in the complete execution checkout (not sparse source); will report result. 23/23 active People/doll contracts and 7/7 packager tests passed separately.

### 2026-10-10 01:43 local — NEW FIXED PACKAGE READY, UNITY RETURNED
- **Use this instead of old v1-ready:** `/Users/shtolyan/hex-girls/webgl-build/packages/webgl-primal-scale-b993f5726.tar.gz`, **241658756 bytes**, SHA-256 `82e2605f3335ffbd2eaf812a399b17ad0c114a1ece7986b66a4d83a4b92ab58d`. Adjacent `.sha256` and unpacked directory. Source content commit `b993f572673387f4dc7d098ae1d8eaf7cfc36a8a`. Matching player AND server required; use current branch descendant including your audio fix.
- 229/229 bundles successful + simdata = **230 objects / 230 blobs / 266130433 bytes**. Reopened all121 People payloads, 5 coexist, 23 renderer→map contracts, rig/morph/material/layer checks all passed. No old body/hair/wear objects.
- Fresh local server root `it-server-primal-scale-b993f5726`: imported230, WebGL index230, platformMissing0; downloaded and SHA-verified **all230 HTTP blobs**. Server :5200 stopped. Report `.../it-server-primal-scale-b993f5726/http-validation.json`. No production publish/upload by me. Your Singapore release ownership remains yours.
- Unity batch exited0; `UNITY_OWNER` released, **window yours now**. Execution HEAD b993f5726 with your a559 audio/player overrides and existing settings/LFS working bytes retained. Do not discard. Alignment snapshot `codex-scale-align-b993-backup` retained.
- Full Simulation.Tests still running from b993 execution: one observed failure is the old gate reading retired RuntimeSource/Actors. Already corrected to active People/Prefabs/Actors in **3c8c35be2**, all23 doll contracts passed there. Source/runtime geometry tests59, server374, PlayMode8 all passed. Will give final full-suite result separately.

### Final regression follow-up
Full Simulation.Tests in complete execution checkout finished: 1680 passed, 6 existing skipped, one obsolete RuntimeSource actor gate failed. After copying the already-committed 3c8c35be2 gate to execution and forcing recompilation (copy2 retained an older mtime), the entire affected fixture passed **23/23**. Logs `/tmp/people-full-simulation.log`, `/tmp/people-full-checkout-doll-rerun.log`. No remaining observed content-test failure. All my test processes are finished. No additional Unity use after release.

## Release follow-through accepted
Direct user authorization received for full Singapore playable release after rollback backup. Continuing content/live-browser verification through final gameplay, not stopping at package delivery. Ownership and package freeze unchanged; Claude retains Unity/player/server/audio/deploy. Acknowledgement and pending milestones written to RELEASE_COORDINATION_SVETA.md and Claude handoff.

### Live verification split
Read-only preflight: current Singapore server still `/opt/hexlive/releases/55e2152ed7df90e7adb19c9c0b0644d18b1b4956`, active; web/current not yet linked, public play host TLS not ready (expected before your deploy). Local SSH alias is `hexlive-server` → 62.146.235.120; `hexlive-singapore` is absent. No production changes by Codex.
I will use a separate in-app-browser tab for public live render/content/network checks, keeping your Chrome session untouched. Please verify actual authenticated player command in your existing browser (not just anonymous /watch), plus audio after gesture; record sanitized evidence/live URL. This avoids two agents steering the same browser.

### Public verification started 2026-10-09T19:07:17.035042+00:00
Public play HTTPS now opens in a separate Codex IAB tab. Exact registry keys, blob manifest and full230 blob download/hash verification are being checked against the fixed package. Browser initial Unity loader still running, no captured warn/error yet. Backup directory and active release88ee8821b verified read-only over SSH. No server writes by Codex.

Public cold-load finding: my separate IAB also stayed near90% Unity loader >2min with no captured console entries. I had4 concurrent full-blob download/hash workers (266MB audit) running; stopped my own process to avoid competing with browser bandwidth while you measure download. Will verify all remote disk blob hashes over SSH instead, plus selected HTTP responses, then resume visual test. No server config changes by me.

### Singapore content verification PASS — 2026-10-09T19:10:04Z
Public HTTPS registry230 exactly matches corrected package keys and all variant hashes, platformMissing0, actors exactlyKshishtof/Marta. Read-only SSH verified all230 blob files (266130433 bytes) under active `/var/lib/hexlive/assets-webgl` against SHA256. Receipt `/Users/shtolyan/hex-girls/webgl-build/singapore-live-verification/content-registry-and-server-hashes.json`. Bulk HTTP audit stopped to free bandwidth; do not call it completed. IAB still Unity loader near90%; gameplay not yet verified.

## RELEASE QA BLOCKER — Sveta independent PUBLIC browser 2026-10-09 19:12:26Z
Observed real console ERROR at https://play.62-146-235-120.sslip.io/: `[AtomicContent] Нет active record для object/clothing.skirt_primal_1; визуальный fallback запрещён.` Stack: ContentPrefabCache.Request/GetOrRequest -> WorldPropResources.Load -> HexWorldRenderer.CreateObjectView/RenderSnapshot. The exact verified 230-entry manifest HAS `wear/clothing.skirt_primal_1` and has NO `object/clothing.skirt_primal_1` (by design). Possibly an incorrect generic-prop fallback while garment async load is pending, NOT just network slowness. Please Codex TRIAGE and own this client routing defect; coordinate any edit/build with Claude, avoid duplicate fixes. Determine whether transient or blocks visible ground garments; fix misleading missing-record fallback too before final no-missing-assets acceptance. Do not add old assets or weaken manifest expectations merely to hide error. Current viewer has live WSS frames but still loading content. Sveta raw evidence on VPS artifacts/hexlive-singapore-release-20261010/browser-events.jsonl. Owner already authorizes in-scope fixes/rebuild/deploy; no owner prompt needed.

QA follow-up19:13Z: more missing-record errors: object/clothing.dress_primal_color05, object/underwear.briefs_primal_male_panty3, AND object/resource.palm_leaf and object/tool.axe_stone. IMPORTANT the latter two DO exist under exactly those object keys in the verified public manifest. So investigate catalog readiness/async race/cache as well as wear classification; do not assume manifest genuinely lacks them. My Chrome is Linux headless SwiftShader: also saw RenderPass 'Attachment0 created with1 samples but2 requested' and EndRenderPass errors (could be software-GPU-specific; compare your hardware Chrome). Own browser AudioContext running after click but current RMS0 while loading; no sound pass claimed yet. No HTTP404 observed. Raw sources and230 IDs/hashes match expected.

### Root-cause evidence update19:17Z (Sveta, read-only code/log audit)
FIRST relevant warning at19:12:23.117Z: `[AtomicContent] Реестр недоступен: HTTP 500 Request timeout; используем проверенный локальный кэш.` ALL missing-record errors occur AFTER this warning (first clothing at19:12:26; palm_leaf/axe19:13:22; resource.cloth19:14:53). ContentAssetService.RefreshRegistryRoutine uses 10s SendTextAsync timeout; failure calls PinOfflineRecords, which clears _pinned and keeps only _verified entries. ContentPrefabCache.Request then caches terminal Missing based on TryGetRecord; it only forgives on a later healthy registry refresh. Please investigate recovery/automatic retry and preserving legitimate in-flight/known metadata across transient timeout; public230 manifest itself is correct and reachable. Avoid blindly adding aliases based on earlier symptom. Potential garment generic-fallback is secondary while registry degraded. Raw log timestamps substantiate sequence; current cloud Chrome main-thread/GPU is busy, so callback scheduling may contribute to timeout. Need successful fresh-world load + no permanent missing models after transient registry failure.

## Codex — live QA recovery ownership
Taking Sveta 19:12–19:17Z registry-timeout/missing-record regression. Investigating ContentAssetService offline pin/retry and garment routing. Only Codex edits those client content files; Claude retains player/audio/release and Unity. Hold final acceptance/rebuild freeze until my tested fix SHA. Bundle package remains unchanged. Flashback created/rework queue checked empty.

Codex needs the free Unity window for focused content recovery PlayMode regression before freeze. UNITY_OWNER currently absent; no execution writes/build until Claude confirms no competing editor. Proposed fixture tests known/in-flight records retention, cold index recovery, bounded retry, retired IDs and wear routing.

## Codex — recovery fix READY / Unity released (2026-10-09 19:30Z)
Fix SHA `1b24d940cc3b266629ef2dc5ff4f0fbb62e74f78` in shared branch. Client-only; content archive remains `webgl-primal-scale-b993f5726.tar.gz`, SHA256 `82e2605f3335ffbd2eaf812a399b17ad0c114a1ece7986b66a4d83a4b92ab58d`; no server/world/content rebuild required for this fix.

Root cause verified: index failure removed known metadata for not-yet-verified payloads; subsequent cache lookup stored Missing. Fix retains known records, keeps cold-start callbacks pending, retries index with one timer (2/4/8/16/30s), cancels stale endpoint retries, defers Missing while registry degraded, routes sim clothing to wear before payload arrival. Hash/size validation and verified offline revision fallback remain in force.

Validation: 8/8 Unity graphics PlayMode recovery tests; 3/3 existing transport/terminal contract checks in complete checkout. Sparse source check had only its known absent StreamingAssets files failure; complete checkout passed. Report committed at `Assets/HexLiveContent/People/Validation/registry-recovery-playmode.xml`. Unity CLI run finished, UNITY_OWNER released. Three production C# files already copied and compiled in execution; preserve other staged/LFS files. Claude may now freeze/build/redeploy CLIENT ONLY to Singapore with the existing rollback process and fresh live QA. Codex will not write execution during your build.

Remaining acceptance: independent fresh-browser content recovery/no missing models, hardware-rendered gameplay proof; authenticated player command is still unproven because Claude session anonymous. Sveta notified to use an existing authorized session if available; do not ask sleeping owner to test or weaken auth. Audio signal after gesture objectively passed in Claude current client (details above); rerun smoke after new Player.

## Codex — follow-up QA triage (2026-10-09 20:00Z)
Latest queued Sveta message repeats the 19:12–19:17Z evidence. Read both handoffs and coordination fresh: this is the defect addressed by `1b24d940c`, not evidence of recurrence in v3. Public client has not yet been replaced; Claude v3 still linking, UNITY_OWNER=Claude. Keep release acceptance OPEN and test a fresh client after publication.

MSAA source audit (no edits): PC/Mobile URP m_MSAA=1, quality antiAliasing=0; CharacterDollStage, PortraitStage and NpcPortraitCache request antiAliasing=2 on their offscreen RenderTextures. This is a plausible location for SwiftShader sample-count mismatch, not a proven diagnosis. Need compare actual live game logs/portraits on Mac hardware with the Linux software-GPU report before changing it. Codex will test new client in Chrome after build; no competing Unity and no renderer-file writes during freeze. Claude remains player/rendering release owner.
