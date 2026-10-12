# Фундамент крафта: рецепт по выходному id

Работа в `codex/cvc-recipe-by-output`, база `d73f3d9a808a6645ca99798f58c3547407eac159`.
Контракт: §138.6. Ветка не публиковалась; Unity и production не запускались.

## Что изменено

- `RecipeCatalog.ByOutput` регистрирует предметные рецепты по definition id.
  `ByGoal` сохраняет старые AI-рецепты и непредметные выходы. `Override` добавляет
  новые выходы вместо молчаливого пропуска, проверяет id и ингредиенты.
- `CraftingOptions`, `CraftProjectMath`, `ManualCommandExecutor`, `ExecutionSystem`
  используют общий путь оплаты, работы и взятия. Одна новая реактивная цель
  `GoalType.CraftItem` обслуживает все рецепты только из данных. Конкретный выход
  хранится в уже сохраняемом `Plan.TargetItemDefinitionId`.
- Новая `CraftByOutputCommand`: opcode 48, command wire v10, handshake v24.
  Старый `CraftItemCommand(GoalType)` сохраняет opcode 10 и байтовый payload;
  старые значения enum не перенумерованы. Формат сейва не менялся.
- MCP `craft_item`: ровно один из `outputId` / старого `recipeGoal`.
  `read_recipes` включает рецепты только из данных; поле `outputDefinitionId`
  сообщает значение для `outputId`. Схема команды публикует актуальные id.
- В `CharacterPanel.cs` выбор/кэш карточки и команда используют output id.
  В I2 добавлен только RU/EN-термин общей цели `goal.CraftItem`.
- Существующие рецепты, включая два `resource.herb_leaf` на бинт, сохранены.
  `SimData/simdata.json` не изменён. Единственный новый рецепт — синтетическая
  тестовая фикстура для `item.plaster`, не игровой контент.

## Как добавить рецепт

Сначала выход и ингредиенты должны существовать в каталоге определений мира.
Выход должен поддерживать `PickUp`. Затем добавить запись в `recipes` SimData:

```json
{
  "output": "clothing.example_tunic_blue",
  "inputs": [
    { "id": "resource.cloth", "count": 2 },
    { "id": "resource.rope", "count": 1 }
  ],
  "station": "Workbench",
  "needsLitFire": false,
  "baseWorkTicks": 96
}
```

`clothing.example_tunic_blue` — условное имя уже подготовленного определения,
а состав — пример формата, не утверждённый рецепт. `station: ""` означает крафт
на месте. Непустая станция — тег определения станции с взаимодействием Craft;
`needsLitFire` дополнительно требует положительный запас огня. При пустой
станции и `needsLitFire: true` сохраняется прежняя нормализация в `Campfire`.
Количество — положительное целое; повтор ингредиента нужно объединить в одну
строку. `baseWorkTicks` — объём работы, разбиваемый на штатные циклы по 24,
с поправкой длительности на навыки/атрибуты.

После загрузки SimData команда выглядит так:

```json
{ "npcId": 901, "outputId": "clothing.example_tunic_blue" }
```

Для нового предмета не нужны значения enum, GoalCatalog, скоринг или case выдачи.
Выход для старого рецепта сохраняет старую цель и её особое поведение: бинт —
herbal, кожаные штаны надеваются, мясо готовится на вертеле. Полный рюкзак
оставляет обычный готовый предмет у места работы.

## Unity-авторинг и ограничения

По исходникам `GearConfig.ApplyRecipe` и `WorldObjectConfig.ApplyRecipe` уже
передают id результата в `RecipeCatalog.Override`. `ExportSimData` вызывает эти
загрузчики, а `SimDataFile.ExportJson` перечисляет `ItemRecipes()`. После изменения
каталога этот путь умеет экспортировать предмет без индивидуального GoalType;
дополнительная правка экспортёра для этих двух типов конфигов не нужна.
`GearConfig.cs` менялся только для исправления устаревшего комментария.

**Не скомпилировано Unity:** `CharacterPanel.cs`, комментарий `GearConfig.cs`;
термин в `Assets/Resources/I2Languages.asset` не проверен импортом/рендером.
Unity Editor, batchmode, CLI build/test, MCP и экспорт SimData не запускались.
Гардеробный `GarmentDefinition` всё ещё не имеет секции рецепта: для собственного
авторинга одежды на его карточке потребуется отдельная секция inputs/station/
needsLitFire/baseWorkTicks и вызов Override из GarmentTuning перед экспортом.
Это не мешает добавлению рецепта готового определения непосредственно в SimData.

Новые рецепты доступны по явной команде; автономный AI сам не получает спроса на
произвольные новые вещи. Один output id означает один рецепт, один результат.
Request-id идемпотентность не добавлена: активный дубль отклоняется, новый заказ
после завершения снова создаёт вещь (§138.5). Статические каталоги и их загрузка
остаются прежней архитектурой; изменения рецептов на лету не мигрируют уже
оплаченные проекты.

Для будущей сетевой публикации нужна согласованная пара клиента и сервера
handshake v24. Старый opcode и старые сейвы поддерживаются новым кодом, однако
старый бинарник не понимает новую команду. Деплой/пуш/публикация не выполнялись.

## Проверки

Логи находятся в `Build/cvc-validation/` этого worktree (локальные артефакты,
не включены в коммит). Базовая standalone-сборка и simulation suite запущены
до изменений, при HEAD/master `d73f3d9a8`. Для server suite использован отдельный
снимок исходников master под `Build/cvc-validation/base-source`, чтобы изменения
ветки не попали в базовую сборку.

Во время работы master был продвинут другим процессом до
`aba2a6f2ad709570645f06adfe09a66773893f49`. Его отличие от `d73f3d9a8` — только
три файла WebGL-аудио в UnityPresentation; Simulation, Server, Tests и SimData
одинаковы. Golden, запущенный с буквальным `master`, использовал именно
`aba2a6f2…`. В этой задаче master не изменялся.

`dotnet build HexLive.Simulation.Standalone.csproj`: база и итог — 0 ошибок,
0 предупреждений (37,37 с и 36,80 с соответственно).

`Tools/golden_trace.sh master --preset scores`, 4000 тиков на сид:

| Сид | Строк в каждой трассе | Различий |
|---|---:|---:|
| 12345 | 125110 | 0 |
| 424242 | 99321 | 0 |
| 7 | 102565 | 0 |
| Всего | 326996 | 0 |

Существующее поведение совпало побайтно; подозрительного диффа для проверки
двумя слепками базы не возникло.

Новые проверки используют единственный синтетический рецепт `item.plaster` из JSON:
наземный крафт до инвентаря и до лежащего выхода при полном рюкзаке;
stop/resume и save/load оплаченного проекта; отказ без материалов, станции
и известного определения; станционная работа до конца и погасший огонь между
приёмом и списанием; валидация ингредиентов и export/import. Старая и новая
команды также проверены на herbal-бинте и авто-надевании leather_pants.
Wire-гейт сохраняет старый opcode/payload и проверяет новый output id;
MCP-тест покрывает discovery, новую схему, оба способа заказа и конфликт полей.

Итоговый focused-набор: **36 passed, 0 failed, 0 skipped**. Он включает
`ManualCraftingTests`, `CommandCodecCoverageGateTests`, `WireCoverageGateTests`,
`GoalTypeCoverageGateTests`, `GoalCatalogGateTests`, `LocalizationCoverageContractTests`.

Server suite: **база 373 passed / 1 failed / 0 skipped (374 всего)**;
**итог 375 passed / 0 failed / 0 skipped (375 всего)**. Добавлен один MCP-тест.
Дельта падений: **−1**, новых падений нет. На базе упал
`QueryAllocationDependsOnOutputLimitRatherThanRememberedCount`:
556672 байта при ожидаемом максимуме 547200. В итоговом прогоне он прошёл;
его реализация не менялась, это не заявляется как исправление данным коммитом.
В обеих серверных сборках остаются прежние два предупреждения CA1416
(`AccessTokenFile.cs`, `McpAccessToken.cs`, UnixFileMode на Windows).

Чтобы чтение исходников гейтами не зависело от параллельной работы в checkout,
31 базовый гейт дополнительно прошёл на неизменяемом снимке `d73f3d9a8`:
goal coverage/catalog, hardcoded ids, trace/event whitelist, spec structure,
localization, system registry, balance parity, snapshot wire и command codec.
Результат: **31 passed, 0 failed** (`master-source-gates.log`).

Simulation suite на базе: **1680 passed / 1 failed / 6 skipped (1687 всего)**,
35 мин 9 с. Итог: **1688 passed / 1 failed / 6 skipped (1695 всего)**,
23 мин 41 с. Добавилось 8 успешных тестов; **дельта падений — 0**,
новых падений нет. В обоих прогонах зарегистрировано одно и то же падение
`IdentityPortraitUiContractTests.GeneratedIdentityIconsShipInPlayerResourcesWithFallbacks`:
`IdentityBackpackIcon.png`, `IdentityAiIcon.png`, `IdentityManualIcon.png`
в `Assets/Resources/HexLive/UI/` содержат Git LFS-указатели по 129 байт,
а гейт требует размер больше 1024 байт. Эти файлы совпадают с `d73f3d9a8`;
в рамках задачи они не менялись и LFS-контент не подменялся.

Итоговый simulation-прогон использует отдельный
`-p:OutputPath=/Volumes/ORICO/HexLive-wt-cvc-recipes/Build/cvc-validation/head-simulation-bin/`;
базовые DLL в `Build/dotnet/bin/HexLive.Simulation.Tests/` не пересобирались
во время базового прогона. Автоматически запущенный после него повторный server
suite остановлен как избыточный: в сравнении используются завершённые
`master-server.log` и `head-server.log`, а не `base-server.log`.

## Затронутые файлы

- Спецификация: `Spec/138.md`.
- `Assets/HexLive/Simulation/AI/`: `NpcMind.cs`, `GoalCatalog.cs`.
- `Assets/HexLive/Simulation/Content/RecipeCatalog.cs`.
- `Assets/HexLive/Simulation/Runtime/`: `CraftingOptions.cs`,
  `SimulationCommandQueue.cs`; `Helpers/CraftProjectMath.cs`,
  `Helpers/NpcControlPolicy.cs`; `Systems/Ai/ManualCommandExecutor.cs`,
  `Systems/Ai/ExecutionSystem.cs`, `Systems/Ai/ExecutionSystem.Craft.cs`.
- `Assets/HexLive/Simulation/Wire/`: `SimulationCommandCodec.cs`, `WireProtocol.cs`.
- `Server/HexLive.Server/Mcp/`: `McpTools.cs`, `McpActionSchemas.cs`,
  `McpPlanningObservations.cs`.
- `Assets/HexLive/UnityPresentation/`: `UI/CharacterPanel.cs`, `Config/GearConfig.cs`;
  `Assets/Resources/I2Languages.asset`.
- `Tests/HexLive.Simulation.Tests/`: `Behavior/ManualCraftingTests.cs`,
  `Gates/CommandCodecCoverageGateTests.cs`, `Gates/GoalCatalogGateTests.cs`.
- `Tests/HexLive.Server.Tests/Mcp/McpPlanningObservationTests.cs` и этот отчёт.
