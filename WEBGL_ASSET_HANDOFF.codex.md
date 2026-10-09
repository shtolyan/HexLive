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
