# #493 — чужой лагерь нельзя редактировать

База: `d0404969b7b7de8b27273baa17a4d4daed24ecad`, существующий checkout MSI.
Поведение: §120.12. Схема из констант: [construction-permissions.svg](Architecture/construction-permissions.svg).

## Граница прав

- Viewer передаёт доверенные назначения своего соединения через `WorldHost`
  в `SimulationEngine`; actorless-команда не получает объединение прав всех игроков.
- Без живого назначенного персонажа стройка запрещена. Удалённый именованный
  вызов без назначений не получает локальный fallback.
- Защищён каждый чужой приватный диск `TheftMath.PrivateGroundRadiusTiles=5`
  (91 гекс с центром, R гекса 1.5 wu), включая пересечения; отношения не дают
  разрешения. Штамп владельца проверяется и вне диска.
- Предварительная проверка покрывает размещение, редактирование, поворот,
  отмену, свободный снос/замену и ручную стройку. Учитываются старый объект,
  новая геометрия, все модули/мебель чертежа и касающиеся гексы общей границы.
  Промежуточные углы yaw квантуются тем же `QuantizeHexSymmetryYaw`, что при создании.
  Ревизия проверяется по неизменному якорю существующего дома, как в
  ApplyBlueprintRevision; подмена клиентом AnchorQ/AnchorR не маскирует чужую землю.
  Мебель проверяется по штатным OccupiedJunctions с JunctionSlot и yaw;
  нейтральный центр гекса не разрешает занятую часть в чужой зоне.
- Весь смешанный жест отклоняется до изменений мира. Регрессионные проверки
  сравнивают полные байты сериализации до/после отказа.
- Новые площадки/модули получают уже поддерживаемый `OwnerFaction` строителя;
  выбор между доступными лагерями — ближайший дом, ничья по ordinal.

## Интерфейс и транспорт

BuildModePanel использует текущие назначения и snapshot для запретного ghost
и доступности редактирования. Свободный конструктор проверяет изменённую
геометрию до изменения локального проекта и истории, в том числе undo/redo.
Серверный отказ доставляется собственному viewer приватным CommandResult,
локально превращается в WorldOrderRejected и отображается по русскому/
английскому термину. Чужая broadcast-трасса отказа не сбрасывает наш редактор.
После отказа свободный редактор восстанавливает authoritative projection и
сбрасывает отклонённый жест/историю. Окончательные права (включая штамп
владельца вне лагеря, не передаваемый текущим snapshot) проверяет сервер.

Wire/save layout, протокол, модели, каталог контента и правила мира не изменены.
Нет миграции или удаления существующих построек.

## Воспроизводимые проверки

```text
dotnet test Tests/HexLive.Simulation.Tests --configuration Release --filter "FullyQualifiedName~ConstructionAccessTests|FullyQualifiedName~FreeArchitectureTests|FullyQualifiedName~BuildingConstructionTests|FullyQualifiedName~Blueprint|FullyQualifiedName~SpecStructureGateTests|FullyQualifiedName~BuildSiteSelectionUiContractTests"
dotnet test Tests/HexLive.Server.Tests --configuration Release
python Tools/webgl_compile_check.py
Unity 6000.4.6f1 CLI PlayMode: HexLive.Tests.ConstructionAccessRuntimeTests
python Tools/generate_construction_access_diagram.py
```

На MSI для net9 тестов с установленным net10 использован только process-local
`DOTNET_ROLL_FORWARD=Major`. Unity и dotnet запускаются последовательно под
соответствующими owner-маркерами; Unity MCP не используется.

ConstructionAccessTests проверяет чужие размещения, снос, поворот, изменение
чертежа и ручную стройку; смешанный жест, implicit replacement, подмену
AnchorTile, общую границу, все шесть поворотов и полу-шаги yaw, два игрока,
пустые назначения, свой/нейтральный участок и историю конструктора.
ConstructionAuthorizationTests гоняет binary-команду через настоящие
локальные duplex WebSockets и ViewerConnection: игрок A не может удалить
модуль B, игрок B может; ручной lease для строительного редактора не нужен.
Внешние сервисы, production и платные голосовые/LLM API не используются.

Локальные сырые логи/результаты: `Build/Diagnostics/20261019-bug493/`.
Финальный целевой native-прогон после заключительного ревью: **127 passed,
0 failed** (`native-after-review.trx`), включая подмену якоря ревизии и шесть
поворотов фактического footprint мебели. Первый целевой прогон дал 120 passed.
Полный серверный набор после ревью: **515 passed, 0 failed, 0 skipped** (`server-after-review.trx`),
в том числе оба новых ConstructionAuthorizationTests и настоящий viewer round-trip.
Строгий WebGL compile после ревью: **0 ошибок компиляции, 0 запрещённых API**
(`webgl-after-review.log`).
Unity 6000.4.6f1 CLI PlayMode на том же кандидате: **2/2 passed, exit 0**
(`unity-construction-access-final.xml/.log`). Проверены реальные методы UI,
кнопки редактирования/удаления, HTML model, запрет жеста/истории и восстановление
проекта после серверного отказа. Первая компиляция нового теста была остановлена
пропущенным using Content; исправлен только импорт namespace и выполнены
повторные успешные запуски. Настройки/материалы Unity восстановлены из снимка.

Первый расширенный прогон был прерван при отсутствии видимого прогресса:
1583 passed, 6 skipped, 0 failed, **run aborted**, не зелёный полный набор.
Последним завершился CharacterPauseContractTests; следующий ClipWindowGate
строит индекс всех `.meta`. Отдельное чтение 24227 файлов заняло 86.1 с.
Причина длительной паузы полного прогона не доказана; дампы памяти не снимались.
Повторный широкий прогон с диагностикой остановлен VSTest по inactivity timeout
5 минут: **1587 passed, 6 skipped, 0 failed, run aborted**. Незавершённый тест
в сохранённом Sequence XML — `ClipWindowGateTests.EveryAttackClipFitsItsSimWindow`.
Набор не считается прошедшим; тест не исключался, gate не ослаблялся, дампы
памяти отключены (`--blame-hang-dump-type none`). Файл теста и UnityAsset.cs
не менялись. Это остаётся ограничением общей проверки и блокером релизного
подтверждения; локальное исправление проверяется целевыми, серверными и UI тестами.

## Ограничения

Серверы, опубликованный Player, Asset API, bundles и latest не изменялись.
Финальная проверка в опубликованной игре требует отдельного обновления
игрового сервера и клиента с этим кодом; новых AssetBundles этот фикс не требует.
