# Bug #393 — эффекты собственного тела через MCP

Статус: реализация, .NET проверки и Unity compiled probe завершены PASS.
Подготовлено к atomic commit и тесту игрока. Владелец
`/root/bug_389`; исходный report не содержит seed/tick/NPC.

Причина: UI читает статусы EffectEvaluator и реальные причины изменения
параметров EffectImpactLedger через snapshot; MCP Describe отдавал только
краткую сводку needs. AgentHost уже передаёт все поля Describe модели.

Общий EffectReadModel теперь выводит статусы и deduplicated ledger triples
одним streaming-проходом. Snapshot пишет прежние строки напрямую, без
промежуточного status-list. List API classifier сохранён. Формулы баланса,
порядок float операций и cadence владельцев ledger не меняются; новые
постоянные поля NPC и tick-вычисления не добавлены.

MCP выдаёт effects, effectImpacts, определения использованных kinds и EN/RU
тексты из I2 export. Переводы статичны на процесс; JSON ответа и временные
коллекции имеют неизбежную стоимость, измеряемую отдельно от чистого reader.
Private effects видимых чужих NPC не раскрываются.

Проверки .NET 9.0.20 (Unity закрыт, Release, seed 393):

- 3/3 EffectReadModel tests: List API и snapshot parity для восьми статусов,
  coma detail, null/clear contract, dedup cadence с сохранением противоположного
  направления, отсутствие мутации ledger/needs, calibrated allocation.
- 19/19 соседних EffectImpactContract tests.
- 8/8 Server tests: четыре новых и четыре соседних MCP observation/privacy.
  Реальные NeedsDecay/Temperature/wound причины совпадают с UI; полный каталог
  kinds/needs и concrete coma/faint/crying/dying detail keys разрешаются в I2.
  Private effects чужого видимого NPC и недоступного тела не раскрываются.
- Последний отдельный payload measurement test 1/1 PASS после уточнения
  сериализации: четыре фактических поля ответа и McpJson.Options; отдельно
  измеряется целый describe_colonist.
- Tools/export_effect_terms.py --check: 132 canonical I2 terms, PASS.

Первая allocation assertion ошибочно требовала полного нуля и обнаружила
96 bytes/call. Разложение 2000 прогретых вызовов: существующий EffectiveUv
192000 bytes; весь новый reader 192000; classifier 0; reader с уже вычисленным
UV 0. Добавленная стоимость reader — **0 bytes/call**. Контрольная аллокация
4120 bytes подтверждает рабочий счётчик. TemperatureSystem не менялся.
Reader median/p95/max: 1.8/2.1/9.0 µs. Начальный failed TRX сохранён рядом с
исправленной проверкой, production код между этими измерениями не менялся.

Профиль ответа (100 warmup + 200 samples, тот же NPC):

| Объект измерения | UTF-8 bytes | allocated bytes/read + JSON | median / p95 / max, µs |
| --- | ---: | ---: | ---: |
| Только четыре поля эффектов с рабочим JSON encoder | 1176 | 4302 | 10.2 / 14.1 / 12446.7 |
| Полный describe_colonist | 6376 | 44208 | 90.6 / 167.8 / 14155.9 |

Первый замер включает самостоятельный Dictionary-envelope для четырёх полей;
он характеризует request-local сборку и сериализацию блока, а не точную дельту
всего Describe. Второй измеряет настоящий McpTools.Call. Max выбросы сохранены;
нулевые JSON allocations или ускорение всего запроса не заявляются.

Изолированный baseline archive
`f07e324a63e8cf0ef69f3df4bcebc3178ac494f2` и candidate запускались
последовательно на том же runtime, seed и content hash. Реальный
WorldSnapshotExporter.Export(world, previous), четыре NPC, 40 warmup + 200
samples: 966222 → 965619 bytes/export (на 603 меньше), effect/impact строки
совпали. Median 2089.4 → 2233.6 µs; p95 12133 → 12361.6 µs; max
12849.3 → 13157.8 µs. Это шумный полный экспорт, ускорение не заявляется;
изменение структурно убирает временный List и кодирование duplicate impacts.

Существующий hexsoak: seeds 12345/424242, по 600 ticks, scores trace и state
hash каждые 200 ticks; baseline/candidate traces побайтно равны:

- 12345: `407D739B5FC5DC186613091C1D637D1923FE6B7D31B8DCBD75749E838D6DDE96`
- 424242: `F04F3B22E3A8D4DE82846DEADAD997A9CECB88683178B8CF402E309558DD81AA`

Воспроизводимые команды и сырые артефакты: `Build/bug393-validation/`,
включая golden-and-snapshot.ps1, snapshot-probe, TRX, reader-profile.json,
mcp-payload-profile.json, snapshot-{baseline,candidate}.json и golden-summary.json.
Editor probe в editor-probe.txt выполнен 2026-09-10 в Unity 6000.4.5f1,
HexLive@a7ec1bd5, под lease /root/bug_389: восемь List/snapshot случаев,
null/clear и ledger dedup/purity — PASS; EffectReadModel загружен из
скомпилированной HexLive.Simulation. Console errors 0. Реальный финальный
state: playing/compiling/updating=false, untitled scene dirty=false, 2 roots.
Без Play, создания/изменения сцен и static tuning; локальный WorldState
использовал активные каталоги редактора. Lease освобождён (FREE).
editor-verification.txt и editor-state-final.txt сохраняют сырые результаты.
До atomic commit баг остаётся in_progress; deployment не выполнялся.
