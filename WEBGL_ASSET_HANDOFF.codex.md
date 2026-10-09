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
