# Production rollout §160 — 2026-09-05

## Развёрнуто

- VPS `62.146.235.120`, systemd `hexlive.service`.
- Viewer: `wss://vmi3529459.contaboserver.net/watch`.
- MCP: `https://vmi3529459.contaboserver.net/mcp`.
- Deployed SHA: `f27053aeecfc1019c517a31fbbf05a676765f9fb`.
- Старый symlink: `/opt/hexlive/releases/11c13ff343ab2702a1e40acf02bc904f89931653`.
- Новый symlink: `/opt/hexlive/releases/f27053aeecfc1019c517a31fbbf05a676765f9fb`.
- Запуск: `--mcp --control --character-preset masha`, прежние save, simdata,
  asset-root, seed и mode. Текущий HugeIsland **не пересоздавался**.
- Generic authored preset, attachment/inbox/phases/turns/chunked WAV, wire v13,
  command arbitration, Deepgram broker, UI, runtime `.vis` и FMOD snapshots.
- Локальный AgentHost опубликован; `masha on|off|status|doctor` не компилируют.
  launchd получает готовый runtime с внутреннего диска
  `~/.local/state/hexlive/agent-masha`, а не с ORICO.
- Маша присоединена к NPC901. При проверке без viewer: `Sleeping`,
  `playerPresent=false`. XAI и выбранный ElevenLabs voice: бесплатный doctor
  получил HTTP 200, генерация для doctor не запрашивалась.
- Локальный workspace и импорт iPhone сохранены; ключи остаются в env 0600,
  не в Unity или release-архиве.

## Подтверждено

- Из чистого release checkout: Server **150 passed**, Simulation **1386 passed,
  6 skipped**, AgentHost **14 passed**. Все три прогона без failed.
- Исключены чужие незакоммиченные renderer/wardrobe/skin изменения; поэтому
  прежний рабочий прогон на 1394 теста не является числом release suite.
- Fake-интеграция: sleep/wake/text/cancel/detach. Русский, Hexkufa и fallback
  `.vis` совпадают с offline golden fixtures.
- Production save v70 → v71: seed 12345, HugeIsland, NPC901 Jana/Marta/masha;
  число NPC/объектов, физическое состояние, одежда/инвентарь сохранены.
  Миграция не возвращает стартовую одежду поверх уже прожитого состояния.
- Финальный остановленный save перед deploy: tick **453124**, blob 70.
- Второй контрольный restart: save tick **453423**, blob 71; журнал подтвердил
  восстановление этого тика и `masha restored as NPC901`, далее тики идут.
- 10/10 живых персонажей; admin/player credential SHA-256 не изменились,
  save и assignments остались `0600 hexlive:hexlive`.
- Публичный HTTPS и Asset API: HTTP 200. Авторизованный WSS upgrade/handshake:
  v13, `AgentIntegrationEnabled=true`, `ControlEnabled=true`, assigned 901.
  Два application ping/pong через VPS: **169 мс** и **109 мс**.
- Unity presentation и test assembly скомпилированы без ошибок до разрешения
  снова открыть Editor. Реальные EditMode/PlayMode не запускались.
- Бутылки #355: отдельный fix-коммит
  `0700dd8a9c1705659fb401054cb8438a7646d6b8`, production содержит его;
  центральный трекер: `ready_for_test`, не `fixed`.

## Deepgram STT: включено, блокер прав устранён

Ключ из `/Users/shtolyan/Desktop/key` безопасно передан через SSH stdin в
`/etc/hexlive/deepgram.env`, `0600 hexlive:hexlive`, подключён drop-in
`10-deepgram.conf`. Но `/v1/auth/grant` дважды вернул HTTP 403:
`FORBIDDEN / Insufficient permissions.` JWT получить не удалось.

По [официальной документации Deepgram](https://developers.deepgram.com/guides/fundamentals/token-based-authentication)
нужен ключ минимум с правами **Member**: API Keys → Create Key → Advanced → Member.

Пользователь добавил второй ключ с административными правами во вторую строку
того же файла. Использована только вторая строка; `/auth/grant` вернул HTTP 200,
JWT TTL 60 секунд. Ключ передан через SSH stdin и атомарно установлен в прежний
`/etc/hexlive/deepgram.env`, `0600 hexlive:hexlive`. Временный disable drop-in
`20-stt-unavailable.conf` удалён; выполнен restart без пересборки бинарника.

Backup конфигурации и финального save:
`/var/backups/hexlive/stt-key-ownu9tei`, root 0700. Мир восстановлен с tick
**457998**, save v71; admin/player credentials не изменились, Маша NPC901 на месте.

Публичный авторизованный `/watch` подтвердил `SttAvailable=true`, wire v13,
assigned NPC901. Через `SttTokenRequest` получен рабочий JWT. Прямой запрос с ним
в Deepgram Nova-3 (`language=ru`, smart_format/punctuate) успешно распознал
**2,50 секунды синтетической русской речи**: HTTP 200, 36 символов, 1531 мс.
JWT и transcript не выводились и не сохранялись; аудио не отправлялось в
игровой сервер или MCP.

После успешной проверки и сверки неизменности исходного файла удалён ровно
`/Users/shtolyan/Desktop/key` с обеими строками. Отсутствие файла проверено;
новый рабочий ключ сохранён в защищённом серверном EnvironmentFile.

Master key по-прежнему не передаётся в Unity. При уже открытом игровом
соединении нужно переподключиться, чтобы получить новый handshake с STT=true.

## Ещё проверить в Editor

1. Войти на production, выбрать Машу: toast attachment, manual toggle disabled;
   после пробуждения — phase/intentSummary и обычный AI между командами.
2. Реальный голосовой ход — FMOD → Deepgram → текст MCP → Grok → ElevenLabs →
   WAV MCP → runtime `.vis` → FMOD/subtitle. Полный путь **ещё не проверен**.
3. Ответ с любого расстояния, mouth sync, Alarm, восстановление snapshots,
   внешний образ после загрузки, EditMode/PlayMode suites.
4. `masha off`: прекращение платных запросов и возврат ручного управления.

## Backup и совместимый recovery

- Backup: `/var/backups/hexlive/agent-v13.rB2Ogs`, root 0700.
  Первоначальный save/config/assignments, отдельный `final-before-switch`
  с финальным состоянием перед deploy, а также post-v71 save.
- Резервный release:
  `/opt/hexlive/releases/742119d9f66bdf2e044b8b1d075305ad4ffe66a0`.
  Новая simulation/save v71/wire v13 + pre-agent server control.
  Server tests: **139 passed**. Чтение и повторное сохранение v71 проверены:
  все сериализованные байты совпали кроме штатно перестраиваемого
  `TopologyVersion` (счётчик инвалидирования кэшей), NPC/предметы не изменились.
- Recovery запускается **без** `--character-preset`/`--companion`: Маша уже
  в сейве. Готовый override в backup: `recovery-override.conf`.
- Откат: выключить AgentHost, остановить service, заменить override указанным
  recovery-конфигом и атомарно переключить symlink на recovery SHA, reload/start.
  **Не откатывать world.sav и не запускать старый v70 reader поверх v71.**
- Main archive SHA-256:
  `48972fff8d7d17f6b838b855ceb6889ad37d52c11043c98b9c6bffa9be0d3219`.
- Recovery archive SHA-256:
  `04a26c71aab1c036db69e2ea26855b6e62068098261e565bc4f4074502f5834a`.
  Суммы проверены на VPS до распаковки. Releases root-owned.

Посторонние dirty renderer/wardrobe/skin файлы и пользовательская crash recovery
scene `Assets/_Recovery/0 (4).unity` не включены в релиз и не удалены.

## Новый мир по просьбе игрока — 2026-09-06

После смерти NPC901 игрок явно запросил новый старт на production. Старый мир
остановлен и сохранён на tick 635679; резервная копия мира, назначений и systemd
находится в `/var/backups/hexlive/new-masha-2r43oxiq` (root, 0700).
Создан свежий HugeIsland, seed 12345, с прежним `--character-preset masha`.
Бинарник `f27053aeecfc1019c517a31fbbf05a676765f9fb`, systemd, simdata,
контент и ключи не менялись; сравнение admin/player credential hashes успешно.

Проверка публичного WSS с существующим player ID из настроек **HexLive.app**
подтвердила wire v13, assigned `[901]`, control/agent/STT=true. MCP подтвердил
единственную живую `profileId=masha`, здоровье 1.0 и обычный AI (GatherWood).
Реестр назначений старого мира сброшен штатным fresh-world startup.
Контрольный restart восстановил новый мир с tick 461 и `masha restored as NPC901`;
следующий WSS handshake на tick 543 снова выдал `[901]`.
Save и assignments остались 0600 hexlive:hexlive.

AgentHost остановлен на время операции и включён после проверок: ON, NPC901,
Sleeping, playerPresent=false. Локальная память и секреты Маши не сбрасывались.
Причина прежней смерти и ошибочно сохранявшийся статус attachment этой
операцией не исправлялись; новый мир не даёт NPC бессмертия.

### Коррекция назначения после проверки игроком

Игрок вошёл в HexLive.app и получил Надю NPC1: ID из локального plist,
использованный первой WSS-проверкой выше, не совпал с ID реального запущенного
клиента. Поэтому та проверка подтверждала назначение проверочному ID, а не
актуальной игровой сессии. Это ошибка проверки, не переименование Маши.

Игроку с NPC1 (единственная такая запись, ID подтверждён по недавнему серверному
connection log) передан NPC901, прежняя привязка NPC901 освобождена. Перед правкой
сохранены world/assignments в `/var/backups/hexlive/assign-masha-ae9c0pv9`.
Штатный restart загружает исправленные назначения **того же нового мира**,
повторного world reset не было. AgentHost оставлен OFF, чтобы запрошенное игроком
ручное управление Машей не блокировалось активным MCP attachment.

### Рюкзак Маши и удаление надетых штанов — 2026-09-06

По явной просьбе игрока у живой NPC901 удалён **один надетый экземпляр**
`FCO Pants Male`; другие экземпляры мира и каталожное определение не удалялись.
Надет один `gear.backpack_riot` (базовый Canvas Backpack, OwnerId=901).
Доступность macOS payload подтверждена в live registry, revision 3, capacity 9.

Через MCP создать/уничтожить предмет нельзя, поэтому операция выполнена при
краткой остановке сервиса на свежем save tick 11758, не на предварительной
inspection-копии. Backup: `/var/backups/hexlive/masha-backpack-DyYdak`.
Использованы DLL сериализатора из действующего production release и точный
effective SimData из публичного handshake. Проверены однозначность предмета,
отсутствие второго рюкзака, обратимость мутации списка без изменения прочего
сериализованного состояния, повторное чтение результата и SHA-256 при доставке.
Сервис запущен с прежним бинарником; сохранение 0600 hexlive:hexlive.
Ранее включённый AgentHost остановлен на время операции и возвращён ON/Sleeping.

Рюкзак также добавлен в исходный стартовый профиль §159.1, без повышения версии
переодевания существующих NPC; 8 профильных тестов прошли. **Эта исходная правка
ещё не развёрнута в production-бинарник** и начнёт действовать для будущих новых
миров со следующим серверным релизом. Текущий рюкзак уже сохранён в live-мире.
