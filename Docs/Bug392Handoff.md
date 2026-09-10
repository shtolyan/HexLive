# #392 — MCP attachment и наблюдаемые нарушения

Владелец: `/root/bug_382`; сохранять этого агента для rework. У отчёта нет seed/tick.
Причина: прежний §160.1 разрешал обычному AI самостоятельные цели между action leases.
Игрок запросил manual-паритет на весь attachment и передачу нарушений агенту.

Реализация:

- Attachment с worldActions хранит transient ExternalNpcControl. Effective ManualControl
  равен сохранённому player switch ИЛИ активному token. Save пишет только raw
  PersistedManualControl; detach/TTL/death/world replace/clear Dispose token без
  изменения player switch. Action lease независим; release/expiry/session-close
  отменяет действие, сохраняя raw flag.
- Takeover останавливает добровольный подход/предупреждение изгнания, не сбрасывает
  уже начатую самооборону или необходимый сон. Повторный attach идемпотентен.
  Standalone MCP после detach очищает disposed token перед обычным acquire.
- AgentIncidentSystem после Perception сообщает нового видимого чужака в лагере
  через личные Agents/Hostiles. Theft/food theft/loot hooks требуют личную видимость
  actor и victim/source; remembered object недостаточен. События адресованы observer,
  без нового обхода объектов мира и без выбора реакции кодом. NeedsDecay hook — FoodStolen.
- AgentHost передаёт observedIncidents модели и запускает critical ход. Очередь64,
  вытеснение явно сообщается. Ошибка provider не потребляет события; durable outbox
  commit потребляет только snapshot запроса. Attach возвращает initial event watermark
  до binding. Prompt требует учесть факты и выбрать реакцию без знания скрытых действий.
- Spec160/117 синхронизированы. Интеграция поверх #389 коммита
  9d4591a4b75fc2116388d9547cf1620feaa23257, его perception API сохранён.

.NET9.0.20 Release, Unity закрыт перед каждой suite:

| Целевая suite | Passed | Failed |
| --- | ---: | ---: |
| Server: MCP/Agent/ControlLeases |115|0|
| AgentHost: Incident/Perception/Action/Outbox |45|0|
| Simulation: ExternalControl/Expulsion/Manual/Reservation/Perception/gates |95|0|

255 targeted tests прошли, compile errors0. Это не full Server/AgentHost: семь
прежних Windows failures документированы в Docs/Bug389Verification.md; baseline
повторно не расследовался. Все24 новые cases прошли: ExternalNpcControl9,
AgentIncidentBuffer4, AgentIncidentConsumption2, registry lifecycle5, full MCP4.
Все16 регрессий #389 тоже прошли: simulation8, server6, host2.

Последняя Unity compile у #383 прошла без ошибок после ground-theft privacy fix;
после READY simulation Assets не менялись. Повторный Editor TestRunner не нужен:
это не визуальный фикс. Результаты не утверждают качество решений живой модели:
provider integration использует управляемый fixture.

Артефакты вне Temp (Unity стирает Temp):
Build/bug392-validation/bug392-Server.trx, bug392-AgentHost.trx,
bug392-Simulation.trx; exact own file manifest — ownpaths.txt там же.
Build/bug392-integration/validate.ps1 содержит фильтры и UnityCount0 guard.
Manifest исключает чужие #394/launcher/ProjectSettings/BUGS.json изменения.

Исправление оформляется отдельным атомарным fix(bug-392) с trailer Bug: #392.
Полный SHA и patch находятся в карточке Singapore. Статус после передачи —
ready_for_test; fixed отмечает только игрок.

Сопутствующий контекст: #382 ready_for_test, commit
414491ee1093994fd74dc8dd806ecc6bc81026e4. #384 in_progress, кода нет: игрок уточнил
«Оставить переливание в свою бутылку; убрать только реальное дублирование».
План runtime own-empty/bottleless — Build/bug384-repro/README.md.
Независимую parked bottle скрывать нельзя.
