# Передача сборки: поштучное строительство

На Mac **не собирались и не публиковались** новые AssetBundles или Player.
Исходники, native FBX, стадии и receipts находятся в master. Порядок ниже
предназначен для ПК-сборщика; скачивать старые бандлы вместо этих шагов нельзя.

## Что забрать

Из корня чистого checkout master (не поверх чужой незавершённой работы):

```powershell
git pull --ff-only origin master
git lfs pull
python Tools/construction_content.py verify-source
```

Ожидается **14 моделей, 299 construction units**. Скрипт только читает файлы:
Unity, Blender, билд и публикацию он не запускает. При LFS-pointer или устаревшем
receipt он останавливается. FBX уже экспортированы: Blender на сборщике не нужен,
старые генераторы поверх них не запускать. Для редактирования использовать
`Docs/ConstructionStagesAuthoring.md`.

## Проверки до контентной сборки

Согласовать Unity с другими агентами. Не запускать второй Editor и не удалять
живой lock. Прочитать актуальные `AGENTS.md`, `CLAUDE.md`, навык `unity-cli`.
Только Unity CLI, никакого Unity MCP. Версия Editor — из ProjectVersion.txt.

```powershell
dotnet test Tests/HexLive.Simulation.Tests --filter "FullyQualifiedName~ConstructionUnitWork|FullyQualifiedName~ConstructionArtContract|FullyQualifiedName~WardrobeStage|FullyQualifiedName~BuildSite|FullyQualifiedName~BuildingConstruction|FullyQualifiedName~FreeArchitecture|FullyQualifiedName~WireCoverage|FullyQualifiedName~SpecStructure"
```

В открытом, свободном Editor вызвать через обнаруженный CLI `eval`:

```csharp
UnityEditor.AssetDatabase.Refresh();
// Дождаться окончания импорта/компиляции, затем отдельным вызовом:
HexLive.Editor.ConstructionStageBuildGate.ValidateAll();
```

Если Editor закрыт и проверочный batch согласован, допустим отдельный запуск
без Player/AssetBundle build:

```powershell
unity run . --timeout 600 -- -nographics -executeMethod HexLive.Editor.ConstructionStageBuildGate.ValidateAll -logFile construction-check.log
```

PASS проверяет каждый ресурсный шаг, нулевую площадку, required nodes, hash FBX
и пути units. Проверочные объекты живут в PreviewScene; активная сцена и Play
Mode не переключаются. Ошибка — запрет продолжать сборку, не повод отключать gate.

## Собрать 14 atomic owners для Windows

Это **действительная сборка**, выполнять только на назначенном сборщике по
поручению игрока. Закрыть Editor перед batch-командами. Из корня репозитория:

```powershell
$constructionDefs = (Get-Content Tools/construction_stage_manifest.json -Raw -Encoding UTF8 | ConvertFrom-Json).definitions
foreach ($constructionDef in $constructionDefs) {
    $contentKind = if ($constructionDef.id.StartsWith('architecture.')) { 'building' } else { 'object' }
    python Tools/content.py build --type $contentKind --id $constructionDef.id --platform StandaloneWindows64
    if ($LASTEXITCODE -ne 0) { throw "Construction build failed: $($constructionDef.id)" }
}
python Tools/construction_content.py verify-candidates --platform StandaloneWindows64
if ($LASTEXITCODE -ne 0) { throw 'Construction content verification failed' }
```

Список команд без запуска: `python Tools/construction_content.py plan`.
Не нужен `build-all`: одежда, люди и звук этой задачей не менялись.
Семь архитектурных owners имеют тип `building`, семь мебельных — `object`;
важен simulation id (`bed.basic`), не имя FBX (`bed_basic_final_native`).

Atomic builder автоматически прогоняет construction gate перед упаковкой,
запрещает подмену main старым prefab и кладёт TextAsset `construction` в тот же
бандл. `candidate.json.metadata.construction` содержит SHA-256 исходного FBX и
все оплачиваемые шаги. `verify-candidates` сверяет полный контракт, платформу,
размер и hash payload; старый кандидат без этого поля проверку не пройдёт.

## Публикация контента — отдельная операция

Сборка не равна публикации. Согласовать конкретный Asset API/сервер и набор
платформ с игроком/ответственным сборщиком. Не деплоить game-server binary и не
перезапускать игровые серверы по этой инструкции.

Общие metadata изменились, поэтому **нельзя молча удержать старые macOS/WebGL
варианты** с другим construction-контрактом. Подготовить matching candidates
для всех обслуживаемых платформ и проверить каждую с `--platform`. Если другой
ПК не умеет собрать одну из них, передать её соответствующему сборщику, а не
выдавать старую за новую. Для WebGL читать `Docs/WebGLBuild.md` и
`RELEASE_COORDINATION_SVETA.md`; не переключать рабочий desktop-checkout на WebGL.
Публикация идёт штатным `Tools/content.py publish`/`publish-all` после проверки
назначения и required platforms. Команд с заранее выбранным production host
эта инструкция намеренно не содержит.

## Новый Player

Бандлы не пакуются внутрь Player. Windows Player собирается отдельно штатным
`python Tools/build_release_windows.py` (`--dry-run` позволяет сначала прочитать
план). Не обходить его gates/версию/bug snapshot; ключ Flashback — локальный ключ
этого ПК, не скопированный с Mac. macOS Player — только на Mac через
`Tools/build_release.py` и обязательное подписание из `Tools/MACOS_SIGNING.md`.
WebGL — только по собственной инструкции, не обычным Build Settings.

Новый клиент увидит новый арт только после успешной публикации соответствующих
atomic revisions через используемый им Asset API. Пересборка одного Player не
обновляет удалённый контент. Проверить в логе ключ owner, platform, revision и
успешную загрузку без legacy/fallback.

В локальной игре новый сим-код попадает в Player. В сетевой игре строительные
действия исполняет **сервер**: если там старый бинарник, новый Player сам по себе
не изменит его поведение. Wire version не менялся; обновление game-server до
этого кода потребует отдельного разрешения игрока на конкретный сервер.

## Приёмка поштучной сборки

1. Новая площадка имеет ноль установленных units, даже если админской командой
   `stock_building` сразу привезена вся смета. `complete_building` — явный cheat,
   не проверка труда NPC.
2. Полный завершённый Build-цикл устанавливает не более одной детали. Доставка
   на середине клипа может перенести пачку, но не включает всю геометрию.
3. У гардероба: 4 каркасных units → 8 досок → 1 обвязка. У кровати:
   4 бревна → 5 палок → 10 верёвок → 50 листьев. Будущая стадия не висит в воздухе.
4. Камни очагов/коллектора добавляются по одному; для жёсткой мебели нужен
   молоток. Завершённый ресурсный bill без установленных деталей ещё не готов.
5. Сохранить частичную стройку, загрузить: установленные units и оставшийся
   запас не меняются, следующее действие добавляет ровно одну деталь. Save v85
   читает прежние поддерживаемые v66–v84 без обнуления старого видимого прогресса;
   старым бинарником новый сейв не открывать.
6. Стены/пол/крыша меняют топологию и готовность от установленных частей, не
   просто от пачки на площадке. Открытие крыши зависит от её опор.
7. Сушилку и гардероб повернуть во всех шести положениях; повесить настоящую
   одежду, обувь и парные перчатки, сохранить/загрузить. Плечики и одежда должны
   оставаться в общем базисе без новых 30°/90° компенсаций.

Артовая витрина: `Assets/Scenes/BuildStagesShowcase.unity`. Поведенческий тест
дома: `Assets/Scenes/HutTest.unity`. Оба читают обычный контент и не заменяют
публикацию. На старых atomic bundles они покажут старые модели.
