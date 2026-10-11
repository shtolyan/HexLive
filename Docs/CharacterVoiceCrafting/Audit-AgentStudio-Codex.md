Аудит выполнен только чтением исходников и документов. [Черновик](<Draft-v0.1.md:1>) прочитан целиком. Файлы не изменялись; тесты, сборки, Unity и сетевые проверки не запускались. Содержимое хранилищ секретов не читалось. Ниже «реализовано» означает наличие кода, а не подтверждённую работу установленной версии.

**Главный результат:** переносимое агентное ядро уже есть. Основная новая работа — серверное создание профилей и управление их жизненным циклом, потоковый голос, серверная авторитетность отношений и полноценный учёт расходов.

## 1. Что делает Agent Studio и что уже переносимо

**Факты**

- Studio — приложение Avalonia, управляющее несколькими агентными профилями. Главное окно создаёт и сохраняет профили, открывает настройки модели/голоса, серверов, интеграций и памяти. Есть переключение RU/EN и темы. [MainWindow.axaml.cs:13](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/MainWindow.axaml.cs:13), [MainWindow.UtilityWindows.cs:39](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/MainWindow.UtilityWindows.cs:39).
- Окно подключения выбирает сервер и **уже существующего NPC** из доступного списка. Это не редактор создания игрового тела. [MainWindow.UtilityWindows.cs:11](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/MainWindow.UtilityWindows.cs:11), [AgentServerConnection.cs:44](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentServerConnection.cs:44).
- Переносимая логика находится в `AgentCore`: профили, конфигурация, провайдеры, управление несколькими агентами, документы и отношения. Причём `AgentCore.csproj` компилирует исходники из каталога `AgentHost`, кроме `Program.cs`. CLI `AgentHost` ссылается на это же ядро. Зависимости от Unity здесь нет. [HexLive.AgentCore.csproj:9](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/HexLive.AgentCore.csproj:9), [HexLive.AgentHost.csproj:11](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/HexLive.AgentHost.csproj:11).
- `AgentFleet` создаёт отдельный controller и неизменяемую конфигурацию запуска для каждого профиля. Controller захватывает файловую блокировку workspace; остановка отменяет запросы и освобождает сессию. [AgentFleet.cs:15](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentFleet.cs:15), [AgentController.cs:33](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentController.cs:33).
- Поток данных: игровой `/watch` → серверный inbox → AgentHost через `/mcp` → наблюдения и память → LLM → проверенное решение → команды/память/отношения → TTS → игровой сервер → клиент. AgentHost — **исходящий MCP-клиент**, собственного HTTP/WebSocket API для браузера у него нет. [AgentHostRuntime.cs:6](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:6), [Spec/160.md:7](/Volumes/ORICO/HexLive/Spec/160.md:7).
- Обычный AgentHost технически пригоден для серверного процесса: .NET 8/9, настройки через environment, обработка SIGTERM. Но проверенный в документации штатный launcher — локальный macOS `launchd`. Факт запуска обычного AgentHost на VPS **не проверен**. [Program.cs:27](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/Program.cs:27), [README.md:20](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/README.md:20).
- `AdminAgentHost` уже имеет серверный Python-процесс и systemd unit. Это отдельный голосовой администратор с инструментами `admin_catalog`, `admin_inspect`, `admin_execute`, а не персонаж с памятью и отношениями. Unit задаёт отдельного OS-пользователя и ограничения доступа. Его фактическая установка сейчас **не проверена**. [admin_agent.py:20](/Volumes/ORICO/HexLive/Server/HexLive.AdminAgentHost/admin_agent.py:20), [hexlive-admin-agent.service:7](/Volumes/ORICO/HexLive/Server/HexLive.AdminAgentHost/hexlive-admin-agent.service:7).

**Вывод:** переносить Avalonia не требуется. Следует использовать `AgentCore` в серверном управляющем сервисе. Desktop-специфичны окна, выбор локальных папок, получение identity из установленной игры и Keychain/Credential Manager.

## 2. Точный контракт профиля и хранения для §3.2

**Факты**

Текущий `AgentProfile` содержит:

```text
Id: Guid
Name: string
Workspace: string
ServerId: Guid
WorldId: string
NpcId: int
Model: { Provider, IntegrationId, ModelId, Reasoning? }
Voice?: { IntegrationId, VoiceId, ModelId }
HeartbeatSeconds: int = 30
DialogueStyleId?: string
```

`ServerProfile` содержит `Id`, `Name`, `McpEndpoint`, `CredentialId`, необязательный `PlayerClientId`. Секреты представлены ссылками, не значениями. **`ownerUserId`, внешности, пола, лагеря, игровых навыков и версии конкретного персонажа в этом DTO нет.** [AgentProfile.cs:6](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentProfile.cs:6).

- Корень Studio вычисляется как `LocalApplicationData/HexLive/AgentStudio`; конфигурация — `profiles.json`. Формат: `{version, agents, servers}`, Web JSON naming, текущая версия контейнера — `1`. Не более 64 агентов и 64 серверов. Revision — SHA-256 всего файла, сохранение использует lock, проверку revision и замену временного файла. Это не БД. [MainWindow.axaml.cs:18](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/MainWindow.axaml.cs:18), [StudioConfigurationStore.cs:6](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/StudioConfigurationStore.cs:6).
- Новый профиль получает новый GUID; стандартный workspace — `workspaces/<guid без дефисов>`. Пользователь может выбрать существующую папку. Имя не используется как имя стандартного каталога. Повтор операции создания выдаёт новый GUID: ключа идемпотентности запроса нет. [MainWindow.axaml.cs:69](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/MainWindow.axaml.cs:69).
- Сначала можно сохранить неполный черновик. Структура памяти создаётся при открытии памяти или запуске runtime. Для запуска обязательны сервер, мир, положительный `NpcId`, имя и модель. [MainWindow.axaml.cs:156](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/MainWindow.axaml.cs:156), [AgentProfile.cs:14](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentProfile.cs:14).
- Инициализатор сохраняет существующую identity; переданная `initialIdentity` применяется только при отсутствии состояния. Для нового Studio-профиля это GUID профиля, введённое имя, возраст 23 и пустые traits. В CLI legacy identity по умолчанию — `masha`. [AgentSessionFactory.cs:19](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentSessionFactory.cs:19), [MashaMemoryStore.cs:698](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryStore.cs:698).

Фактическая структура workspace, часть файлов создаётся по мере использования:

```text
<workspace>/
  SOUL.md
  USER.md
  MEMORY.md
  memory/
    worlds/<safe episode id>.md
    daily/<YYYY-MM-DD>.md
    imports/
    archive/<YYYY-MM-DD>.jsonl
    legacy-self.md
    legacy-soul/
  .state/
    state.json
    backups/
    document-edits/
    view-revisions/
    search/
    archive-migration-v1.json
    runtime/                     # расположение, выбранное Studio
      agent-host-status.json
      agent-outbox.json
      diagnostics/events*.jsonl
  .history/<hash document path>/*.md
  .agent-studio.lock
  .documents.lock
```

Основания: [MashaMemoryWorkspace.cs:41](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryWorkspace.cs:41), [AgentMemoryArchive.cs:107](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentMemoryArchive.cs:107), [WorkspaceDocuments.cs:61](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/WorkspaceDocuments.cs:61), [AgentMemorySearch.cs:51](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentMemorySearch.cs:51), [AgentHostOptions.cs:27](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostOptions.cs:27).

- `.state/state.json` хранит identity, собеседников, отношения, факты, эпизоды миров, цель, план, прогресс и ID применённых операций. Поддерживаются версии архива `1..2`; привязка собеседников переводит архив в v2. Есть импорт legacy `memory-v1.json`. [MashaMemoryStore.cs:76](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryStore.cs:76), [SpeakerMemory.cs:62](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/SpeakerMemory.cs:62).
- Повторное открытие существующего workspace сохраняет данные; повреждённый JSON вызывает ошибку. Но общей транзакции «профиль + папки + NPC + предметы» нет. Даже первичная запись нового `state.json` выполняется отдельно от `profiles.json`. [MashaMemoryStore.cs:131](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryStore.cs:131), [MashaMemoryStore.cs:743](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryStore.cs:743).

**Вывод:** §3.2 должен различать **новый серверный CharacterProfile** и **совместимый workspace агента**. GUID Studio, `identity.id` памяти и `worldId + npcId` — разные идентификаторы; автоматически считать их одним `characterId` нельзя. Идемпотентное создание героя и согласованность БД/файлов/мира предстоит реализовать.

## 3. Характер, шаблоны и системный промпт

**Факты**

- Авторские тексты находятся в `Server/HexLive.AgentHost/Prompts`: `rules.md`, `assessment.md`, `masha-style.md`, `language.md`, `soul.md`, `memory.md`, `world-guidance.md`, `object-knowledge.md`, `text.json`. При сборке копируются в `AgentPrompts` рядом с executable. Читаются из файлов без необходимости перекомпиляции. [Prompts/README.md:6](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/Prompts/README.md:6), [AgentPromptFiles.cs:12](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentPromptFiles.cs:12).
- Сборка системных инструкций: общие rules → assessment → выбранный стиль/примеры → language. Затем провайдер добавляет точную схему ответа, память и текущие данные мира/реплики. [AgentPromptBuilder.cs:10](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentPromptBuilder.cs:10), [AgentProviders.cs:96](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentProviders.cs:96).
- Подтверждён **один именованный стиль** — `masha-sharp-v1`; неизвестные style ID отклоняются. Есть общий шаблон `soul.md`, подставляющий имя, возраст и traits. Каталога нескольких готовых характеров в проверенном агентном коде нет. [DialogueStyles.cs:11](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/DialogueStyles.cs:11), [AgentProfile.cs:16](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentProfile.cs:16), [MashaMemoryWorkspace.cs:322](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryWorkspace.cs:322).
- Личность `SOUL.md` отделена от модельных обновлений памяти: `memoryUpserts` не должны переписывать душу; старые model-self заметки архивируются отдельно. [Prompts/rules.md:57](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/Prompts/rules.md:57), [MashaMemoryWorkspace.Soul.cs:9](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryWorkspace.Soul.cs:9).
- Реальные полномочия ограничиваются whitelist, схемой действий и серверной авторизацией. Но **общие текстовые правила сейчас тоже редактируемые файлы**, а не версионированная серверная политика с отдельными правами доступа. [Spec/164.md:9](/Volumes/ORICO/HexLive/Spec/164.md:9), [AgentActionContract.cs:19](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentActionContract.cs:19).

**Вывод:** разделение личности, памяти и технических полномочий уже заложено. Для WebGL нужны отдельный каталог характеров, версия базовой политики и ограниченный API редактирования личности. Обещание «существующих шаблонов» во множественном числе кодом пока не подтверждается.

## 4. LLM: провайдеры, модели, streaming и ключи

**Факты**

- Studio поддерживает OpenAI, Grok, Claude, DeepSeek и Codex. HTTP-адаптер использует соответственно Responses, xAI chat completions, Anthropic Messages и DeepSeek chat completions. [AgentProfile.cs:3](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentProfile.cs:3), [HttpModelAdapter.cs:59](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/HttpModelAdapter.cs:59).
- Модель выбирается в настройках профиля из загруженного каталога. `ModelId`, integration и reasoning сохраняются отдельно для каждого профиля. [ProfileSettingsWindow.cs:56](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/ProfileSettingsWindow.cs:56), [ProfileSettingsWindow.cs:124](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/ProfileSettingsWindow.cs:124).
- В standalone CLI выбор уже: `HEXLIVE_AGENT_LLM=xai|codex`; xAI default — `grok-4.20-0309-reasoning`. Default Codex runner — `gpt-5.6-terra`, `low`; Studio может передать выбранную модель вместо default. Админский host — `deepseek-v4-pro` либо `gpt-6-astra`. Это литералы кода, **доступность этих моделей сейчас не проверена**. [AgentHostOptions.cs:72](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostOptions.cs:72), [CodexDecisionRunner.cs:8](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/CodexDecisionRunner.cs:8), [CodexModelAdapter.cs:13](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/CodexModelAdapter.cs:13), [admin_agent.py:328](/Volumes/ORICO/HexLive/Server/HexLive.AdminAgentHost/admin_agent.py:328).
- `GPT-Luna`/отдельный `luna` в проверенных агентных исходниках не найден. Динамический каталог теоретически может вернуть дополнительный ID; локальный аудит этого не устанавливает.
- Потоковых LLM-ответов нет: HTTP-адаптер буферизует ответ и разбирает законченный JSON. Codex возвращает готовый файл решения; админский DeepSeek явно задаёт `stream: false`. [HttpModelAdapter.cs:152](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/HttpModelAdapter.cs:152), [CodexDecisionRunner.cs:46](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/CodexDecisionRunner.cs:46), [admin_agent.py:254](/Volumes/ORICO/HexLive/Server/HexLive.AdminAgentHost/admin_agent.py:254).
- Studio использует Keychain/Credential Manager с service `HexLive.AgentStudio`; типичные reference ID — `model.OpenAI`, `model.Grok`, `model.Claude`, `model.DeepSeek`, `voice.ElevenLabs`. Linux-реализации этого secret store нет. [OperatingSystemSecretStore.cs:10](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/OperatingSystemSecretStore.cs:10), [MainWindow.axaml.cs:43](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/MainWindow.axaml.cs:43).
- Документированные пути CLI: `~/.config/hexlive/agent-masha.env`, отдельный `~/.config/hexlive/servers/62.146.235.120/mcp-token`; переменные `XAI_API_KEY`, `ELEVENLABS_API_KEY`, `HEXLIVE_MCP_TOKEN`. Админский host: `/etc/hexlive/admin-agent-token.env`, `/etc/hexlive/deepseek-admin.env`. Наличие и содержимое этих файлов в текущих установках не проверялось. [README.md:43](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/README.md:43), [AdminAgentHost/README.md:35](/Volumes/ORICO/HexLive/Server/HexLive.AdminAgentHost/README.md:35).

**Вывод:** сервер может повторно использовать интерфейсы провайдеров, но потребуется серверное хранилище секретов и выбор разрешённых моделей. Luna нельзя фиксировать как проверенную интеграцию; streaming необходимо добавлять.

## 5. Голос, ElevenLabs, STT, прерывания и задержки

**Факты**

- В Studio привязка голоса — `AgentProfile.Voice`: integration, `VoiceId`, TTS `ModelId`; она сохраняется в `profiles.json`. Default TTS model в форме — `eleven_multilingual_v2`. [AgentProfile.cs:9](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentProfile.cs:9), [ProfileSettingsWindow.cs:21](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/ProfileSettingsWindow.cs:21).
- Есть отдельный авторский каталог голосов для генерации игровых реплик: Jana, Jolly, Marta, Molly, Masha, Kshishtof. Например, Masha → `NsFK0aDGLbVusA7tQfOB`; этот же ID — default CLI. **Каталог заготовленных реплик и выбор голоса Studio — два отдельных источника**, не единая серверная привязка персонажа. [voices.json:5](/Volumes/ORICO/HexLive/_ArtSource/Voice/voices.json:5), [AgentHostOptions.cs:74](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostOptions.cs:74).
- ElevenLabs вызывается через `/v1/text-to-speech/{voiceId}?output_format=pcm_24000`. Код ждёт весь PCM, пересэмплирует в 44,1 kHz и создаёт WAV. Conversational AI/Agents API в проверенном контуре не используется. [AgentProviders.cs:436](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentProviders.cs:436).
- Только после готовности WAV идут `begin_agent_utterance` → чанки по 128 KiB → `commit_agent_utterance`. Это доставка законченного файла, **не воспроизведение по мере генерации TTS**. [AgentHostRuntime.cs:678](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:678).
- STT — Deepgram Nova-3, prerecorded HTTP POST полного WAV, `language=ru`. Клиент обращается к провайдеру напрямую. Серверный broker выдаёт временный JWT на 60 секунд, максимум четыре выдачи в минуту на control owner; master key — `HEXLIVE_DEEPGRAM_API_KEY`. [DeepgramSpeechTranscriber.cs:24](/Volumes/ORICO/HexLive/Assets/HexLive/UnityPresentation/Audio/DeepgramSpeechTranscriber.cs:24), [DeepgramTokenBroker.cs:13](/Volumes/ORICO/HexLive/Server/HexLive.Server/DeepgramTokenBroker.cs:13).
- В спецификации текущего захвата: ручное завершение либо 3 секунды тишины; на запись звук игры приглушается до mute. Это текущий режим записи реплик, а не полный дуплекс с эхоподавлением. Реальное выполнение клиентом этих правил в данном аудите не проверялось. [Spec/160.md:167](/Volumes/ORICO/HexLive/Spec/160.md:167).
- Новая реплика отменяет незавершённое **автономное** рассуждение. Пока занят reply-slot, следующая порция inbox не читается. Завершённое действие живёт отдельно от отмены модельного хода. Полноценное barge-in, останавливающее текущий голосовой ответ при начале речи игрока, этим механизмом не реализовано. [AgentHostRuntime.ReplyScheduling.cs:45](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.ReplyScheduling.cs:45), [AgentHostRuntime.ReplyScheduling.cs:149](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.ReplyScheduling.cs:149).
- Есть замеры времени модели, подготовки речи и всего хода, но не подтверждённые p50/p95 «конец речи → первый звук в браузере». Дополнительно inbox опрашивается раз в 500 мс. [AgentHostRuntime.cs:449](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:449), [AgentHostRuntime.cs:503](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:503).

**Вывод:** существующий код соответствует варианту **B**, причём пока без потокового STT/LLM/TTS. Вариант A — новая интеграция. Цели 1,5/3 секунды не подтверждены; при автоматическом завершении записи текущие 3 секунды тишины уже расходуют весь предложенный p95-бюджет.

## 6. Путь от реплики к действию, права и идемпотентность

**Факты**

- Runtime получает финальный текст, актуальные `world_status`/`describe_colonist`, память и MCP-каталог; модель возвращает структурированное решение. Затем проверяются схема, допустимый tool и аргументы. Исполнитель `npcId` принудительно подставляется из привязки, не доверяется модели. [AgentHostRuntime.cs:307](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:307), [AgentActionContract.cs:19](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentActionContract.cs:19).
- Whitelist включает `move_to`, `interact`, `craft_item`, `stop`, `talk_to`, помощь/лечение, перенос людей, inventory operations, `attack_mob`, `merge_camps`, `request_item`, `rest_until`; `query_known_objects` — чтение. Произвольных MCP/OS-команд здесь нет. [AgentProviders.cs:25](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentProviders.cs:25).
- На игровом стыке MCP проверяет владельца NPC; физический action получает control lease. Исполнение идёт через штатную серверную шину, а не напрямую меняет состояние мира. [McpEndpoint.cs:155](/Volumes/ORICO/HexLive/Server/HexLive.Server/Mcp/McpEndpoint.cs:155), [AgentHostRuntime.cs:730](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:730), [WorldHost.AgentCommands.cs:76](/Volumes/ORICO/HexLive/Server/HexLive.Server/WorldHost.AgentCommands.cs:76).
- Есть два режима исполнения. Одиночный `action` вызывает игровой tool напрямую и отслеживает outcome по событиям/состоянию. Сохраняемый execution plan использует `execute_agent_command` и `read_agent_command` с `commandId + sequence`. [AgentHostRuntime.cs:721](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:721), [AgentHostRuntime.ExecutionPlan.cs:123](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.ExecutionPlan.cs:123).
- Для tracked-команд сервер хранит fingerprint, sequence и результат; повтор старого sequence не исполняется заново, подмена аргументов/ID даёт conflict. При неизвестном исходе runtime сверяет квитанцию, не повторяет вслепую. Есть тесты replay после save/reload и проверки scope/lease — **прочитаны, не запускались**. [WorldHost.AgentCommands.cs:56](/Volumes/ORICO/HexLive/Server/HexLive.Server/WorldHost.AgentCommands.cs:56), [AgentCommandReceiptTests.cs:167](/Volumes/ORICO/HexLive/Tests/HexLive.Server.Tests/Mcp/AgentCommandReceiptTests.cs:167), [AgentCommandReceiptTests.cs:337](/Volumes/ORICO/HexLive/Tests/HexLive.Server.Tests/Mcp/AgentCommandReceiptTests.cs:337).
- Outbox гарантирует повторную доставку commit памяти/отношений; восстановление outbox не воспроизводит физический action и речь. Это защищает от части дублей, но не даёт общей гарантии доставки одиночного действия после сбоя между commit и dispatch. [AgentHostRuntime.cs:451](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:451), [AgentHostRuntime.cs:576](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:576).
- Действие запускается **до TTS**, что уже соответствует требованию действовать во время разговора. Но speech генерируется до результата действия. При отказе предварительной проверки код очищает `decision.Action`, не переписывая speech. Следовательно, честный окончательный ответ поддерживается feedback и промптом, но не обеспечен обязательным «результат → новая реплика». [AgentHostRuntime.cs:434](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:434), [AgentHostRuntime.cs:485](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:485).

**Вывод:** адаптер команд уже существенный. Для черновика нужно унифицировать все изменяющие действия вокруг durable `actionId/commandId`, определить восстановление после сбоя и отдельно формировать подтверждение результата. Численное доверие сейчас влияет прежде всего через промпт; отдельного общего технического допуска действий по trust в проверенном адаптере нет.

## 7. Память и отношения: хранение и источник истины

**Факты**

- Память хранится в `.state/state.json`, Markdown-представлениях и JSONL-архиве событий. Краткое состояние ограничено; полный полученный текст и события уходят в архив с источником, эпизодом, собеседником и временем. Поиск использует перестраиваемый файловый индекс. [MashaMemoryStore.cs:76](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryStore.cs:76), [AgentMemoryArchive.cs:9](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentMemoryArchive.cs:9), [AgentMemorySearch.cs:29](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentMemorySearch.cs:29).
- Отношения — числа: Familiarity и Trust `0..1`, Sympathy/Affinity `−1..1`. Новый bond нулевой. Модель предлагает направление изменения, код применяет фиксированные ограниченные шаги и дедупликацию сообщений. [MashaMemoryStore.cs:16](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryStore.cs:16), [VoiceRelationship.cs:35](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/VoiceRelationship.cs:35).
- Отношения разделены по собеседникам. `SpeakerKey` строится из хеша MCP endpoint и GUID sender; распознавания личности по тембру нет. Смена endpoint изменяет namespace отношений. Строки вроде `npc:<id>` эта функция сводит к `unidentified`, поэтому переносить её на отношения между NPC без доработки нельзя. [AgentSpeakerRuntime.cs:11](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentSpeakerRuntime.cs:11).
- В промпт добавляется обязательный численный блок отношений; он не берётся из произвольной биографии. На игровом сервере отношения голоса представлены временным `relationView`; серверный Social — отдельная величина. [MashaMemoryWorkspace.cs:158](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryWorkspace.cs:158), [Spec/160.md:91](/Volumes/ORICO/HexLive/Spec/160.md:91).
- **Автор Studio может изменить численные отношения через `USER.md`.** Обработчик сравнивает строку процентов и записывает новые значения в bond основного собеседника. Это явно покрыто тестом. [MemoryDocumentEdits.cs:156](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MemoryDocumentEdits.cs:156), [MemoryDocumentSyncTests.cs:65](/Volumes/ORICO/HexLive/Tests/HexLive.AgentCore.Tests/MemoryDocumentSyncTests.cs:65).
- Импорты legacy/Molly сохраняют прежние отношения и биографию, поэтому «нулевые отношения» верно для нового состояния, но не для импортированного. Исходников действующего `MollyBridge` в его каталоге не обнаружено; актуальна совместимость через импорт AgentHost. [MashaMemoryStore.cs:348](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/MashaMemoryStore.cs:348), [SpeakerMemory.cs:76](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/SpeakerMemory.cs:76).

**Вывод:** численная модель уже есть, но её авторитетность сейчас локальная и допускает авторские правки. Нужно перенести состояние под серверное владение, запретить игроку редактирование метрик, определить миграцию старых отношений и стабильные ID собеседников. Перенос архива также означает перенос приватных текстов разговоров — это отдельное решение о хранении.

## 8. Учёт расходов

**Факты**

- `ModelAnswer` умеет возвращать input/output tokens. HTTP-адаптер извлекает их из provider `usage`; AgentProviders связывает usage с провайдером и выбранной моделью. [AgentProfile.cs:44](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentProfile.cs:44), [HttpModelAdapter.cs:131](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/HttpModelAdapter.cs:131), [AgentProviders.cs:115](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentProviders.cs:115).
- Runtime пишет `model.request.completed` с usage и временем. Диагностика содержит session ID, корреляции профиля/мира/хода и NPC. Но это локальные ротируемые JSONL: default 8 файлов по 8 MiB и retention 30 дней; ошибка записи не останавливает игру. [AgentHostRuntime.ObjectKnowledge.cs:71](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.ObjectKnowledge.cs:71), [AgentDiagnostics.cs:27](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentDiagnostics.cs:27).
- В диагностике нет `ownerUserId`, цены, валюты, версии тарифа, provider request ID, cached tokens или оплачиваемых единиц аудио. Codex adapter возвращает ответ без token counts. [AgentDiagnostics.cs:93](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentDiagnostics.cs:93), [CodexModelAdapter.cs:16](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/CodexModelAdapter.cs:16).
- TTS возвращает только WAV; STT-клиент извлекает только transcript. Длительность WAV для транспорта не превращается в финансовую запись. [AgentProviders.cs:9](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentProviders.cs:9), [DeepgramSpeechTranscriber.cs:40](/Volumes/ORICO/HexLive/Assets/HexLive/UnityPresentation/Audio/DeepgramSpeechTranscriber.cs:40).
- Ошибочные/отменённые запросы записываются без usage; оплаченный ответ с невалидным решением также может не дойти до записи usage. Полного журнала тарифицируемых попыток нет. [AgentHostRuntime.ObjectKnowledge.cs:81](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.ObjectKnowledge.cs:81), [AgentProviders.cs:115](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentProviders.cs:115).
- `AgentDialogueEval` — стенд качества с реальными LLM-вызовами, синтетической памятью, без выполнения MCP и TTS. Он фиксирует ответы и длительность, но не заменяет usage ledger или сквозной голосовой benchmark. [AgentDialogueEval/README.md:3](/Volumes/ORICO/HexLive/Tools/HexLive.AgentDialogueEval/README.md:3).

**Вывод:** есть полезная телеметрия токенов, но подсистемы расходов из §6 черновика нет. Нужен отдельный серверный журнал всех provider requests, включая ошибки, отмены и дополнительные модельные раунды; затем тарифы, агрегаты, лимиты и отдельный ledger кредитов.

## 9. Авторизация, pairing и многопользовательность

**Факты**

- Identity — отдельный ASP.NET-сервис. Хранилище — `${HEXLIVE_IDENTITY_DATA}/accounts.json`, с единственным writer lock; в аккаунте есть стабильный ID, hash ключа, permissions, revision, revoked и subject type. Это JSON, не SQL-БД. [Identity/Program.cs:18](/Volumes/ORICO/HexLive/Server/HexLive.Identity/Program.cs:18), [KeyStore.cs:9](/Volumes/ORICO/HexLive/Server/HexLive.Identity/KeyStore.cs:9).
- Identity выдаёт и ротирует ключи, отзывает их, ведёт аудит прав. Endpoint `/api/identity/v1/validate` возвращает account ID и permissions. Это управление доступом по ключам; готовой пользовательской регистрации/входа для нового браузерного продукта в прочитанном сервисе нет. [KeyStore.cs:37](/Volumes/ORICO/HexLive/Server/HexLive.Identity/KeyStore.cs:37), [IdentityValidation.cs:33](/Volumes/ORICO/HexLive/Server/HexLive.Identity/IdentityValidation.cs:33).
- При подключённой Identity MCP определяет игрока через `principal.AccountId`, а админа — по `server.admin`. Player session привязана к миру и разрешает только принадлежащих игроку NPC. [McpEndpoint.cs:70](/Volumes/ORICO/HexLive/Server/HexLive.Server/Mcp/McpEndpoint.cs:70), [McpEndpoint.cs:155](/Volumes/ORICO/HexLive/Server/HexLive.Server/Mcp/McpEndpoint.cs:155).
- Постоянное владение NPC находится отдельно от временного control lease. В спецификации — `playerId → [npcId…]`, файл назначений рядом с миром; identity аккаунта общая, назначения относятся к конкретному миру. [Spec/149.md:10](/Volumes/ORICO/HexLive/Spec/149.md:10).
- Несмотря на имя, **текущий `ServerPairingWindow` не выполняет кодовое pairing**. Он принимает endpoint и существующий игровой токен, получает `HexLive.RemoteClientId` из установленной desktop-игры и проверяет доступ. Это явная локальная зависимость. [ServerPairingWindow.cs:8](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/ServerPairingWindow.cs:8), [GameClientIdentity.cs:10](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/GameClientIdentity.cs:10).
- Legacy pairing API сохранился: двухминутный ticket, подтверждение владельцем, grant с hash credential, сессии на пять минут. Но в MCP-ветке с подключённой Identity legacy grants не используются. [McpPlayerAccess.cs:47](/Volumes/ORICO/HexLive/Server/HexLive.Server/Mcp/McpPlayerAccess.cs:47), [McpEndpoint.cs:80](/Volumes/ORICO/HexLive/Server/HexLive.Server/Mcp/McpEndpoint.cs:80).
- Несколько профилей и собеседников поддержаны, но `AgentFleet` — локальный менеджер: в его API нет tenant/user authorization. Админский host хранит краткую историю отдельно по `clientId + epoch`, однако имеет принципиально другие полномочия. [AgentFleet.cs:15](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentFleet.cs:15), [admin_agent.py:349](/Volumes/ORICO/HexLive/Server/HexLive.AdminAgentHost/admin_agent.py:349).

**Вывод:** Identity и серверная проверка владения — подходящая основа. Нужен серверный слой, связывающий авторизованный аккаунт, character ID, workspace и runtime. Нельзя использовать desktop client ID как единственное доказательство личности или выдавать персонажному runtime админский доступ.

## 10. Перенос, риски и расхождения с черновиком

**Факты и оценка**

- **Runtime можно использовать повторно**, но сейчас он не предоставляет browser API, не создаёт героя и зависит от файловых блокировок. Для нескольких серверных процессов потребуются явное владение запуском и защита от двух runtime одного персонажа. [AgentController.cs:40](/Volumes/ORICO/HexLive/Server/HexLive.AgentCore/Studio/AgentController.cs:40), [AgentHostRuntime.cs:6](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:6).
- **Отключение игрока не останавливает платную автономию.** В текущем runtime активность определяется паузой мира; `playerPresent` только наблюдается. Требование закрывать оплачиваемую голосовую сессию при disconnect нуждается в отдельной политике, особенно если автономный агент продолжает жить. [AgentHostRuntime.cs:226](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentHostRuntime.cs:226).
- **Одна реплика может вызвать несколько LLM-запросов:** поиск памяти, справочные чтения и исправление невалидного плана. Цикл допускает до семи итераций, deadline — 120 секунд либо 300 при активной/приостановленной цели. Это важно для задержек и стоимости, хотя не означает два одновременно работающих LLM-контура. [AgentMemoryRecall.cs:27](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentMemoryRecall.cs:27), [AgentMemoryRecall.cs:78](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/AgentMemoryRecall.cs:78).
- **WebGL-голос не подтверждён.** Прочитанный STT-клиент использует `HttpClient` и готовый WAV; спецификация WebGL отдельно отмечает непроверенную доставку ответа агента через временный файл. Браузерный microphone/streaming gateway нельзя считать готовым из наличия desktop-голоса. [DeepgramSpeechTranscriber.cs:24](/Volumes/ORICO/HexLive/Assets/HexLive/UnityPresentation/Audio/DeepgramSpeechTranscriber.cs:24), [Spec/168.md:132](/Volumes/ORICO/HexLive/Spec/168.md:132).
- Существующие тесты проверяют изоляцию профилей, отношения, provider contracts, отмену автономии, действие до TTS и replay команд. Они дают основу регрессии, но их актуальный результат и сквозной WebGL-сценарий **не проверены**. [ProfileIsolationTests.cs:32](/Volumes/ORICO/HexLive/Tests/HexLive.AgentCore.Tests/ProfileIsolationTests.cs:32), [AgentReplySchedulingTests.cs:11](/Volumes/ORICO/HexLive/Tests/HexLive.AgentHost.Tests/AgentReplySchedulingTests.cs:11).

**Что в черновике расходится с текущим кодом**

| Положение черновика | Что обнаружено |
|---|---|
| §3.2: взять существующий контракт профиля Studio | Это конфигурация агента для существующего NPC; серверного `CharacterProfile` с owner/appearance/camp/skills там нет. |
| §2/§5: выбор из существующих характеров | Проверены общий SOUL-шаблон и один именованный стиль Маши. |
| §3.2/§4.2: отношения авторитетны на игровом сервере и не меняются редактированием | Сейчас они авторитетны в workspace; редактор `USER.md` специально изменяет численные метрики. |
| §4.1: ElevenLabs обеспечивает разговорный контур | Текущая интеграция — только TTS; Conversational AI не подключён. |
| §4/§8: streaming и перебивание | Сейчас законченные STT/LLM/TTS-ответы и загрузка готового WAV; отмена автономного reasoning не равна barge-in. |
| §4: повтор `actionId` безопасен для всех действий | Tracked execution plan имеет такую основу; одиночный action идёт другим путём. |
| §4: реплика сообщает результат исполнения | Speech формируется до исполнения; обязательного подтверждения результата перед произнесением нет. |
| §6: расходы по пользователю/персонажу/сессии | Есть локальная диагностика части LLM usage, финансового ledger нет. |
| §4.3: оплачиваемая сессия закрывается при уходе игрока | Runtime продолжает автономные ходы, пока мир не поставлен на паузу. |

Основания для строк таблицы приведены в разделах 2–9 выше. Это различия между будущими требованиями и реализацией, а не утверждение, что черновик ошибочно объявляет их уже готовыми.

Отдельно документация местами устарела: README AgentHost обещает сон без игрока и историю только в RAM, тогда как текущий код ориентируется на паузу мира и сохраняет разговоры. `IMPLEMENTATION.md` содержит исторические результаты проверок и незавершённые пункты; использовать его как доказательство текущего production нельзя. [README.md:24](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/README.md:24), [README.md:69](/Volumes/ORICO/HexLive/Server/HexLive.AgentHost/README.md:69), [IMPLEMENTATION.md:3](/Volumes/ORICO/HexLive/Server/HexLive.AgentStudio/IMPLEMENTATION.md:3).

**Вывод:** весь агентный объём — **L**, хотя большая часть поведения агента пригодна для повторного использования. Наиболее существенные риски: смешение идентификаторов и владельцев, редактирование trust через память, частичная идемпотентность действий, отсутствие потокового голоса и неполный учёт оплаченных запросов. S/M/L ниже — инженерная оценка объёма изменений, не календарный срок; игровой worldgen, одежда и рецепты вне данного аудита.

| Компонент | Где сейчас | Что нужно для серверного персонажа | Объём |
|---|---|---|---|
| Desktop-окна Studio | Avalonia на компьютере игрока | Заменить нужные пользовательские сценарии WebGL/API | M |
| Character/Profile service | Отсутствует в проверенной агентной части | Owner, character ID, внешность/навыки/лагерь, версии, идемпотентное создание | L |
| Agent runtime и fleet | Переносимый AgentCore, локальное управление | Серверный supervisor, восстановление, один активный runtime на героя | M |
| Workspace и память | JSON/Markdown/JSONL на диске агента | Серверное размещение, права, миграции, backup, политика хранения | M |
| Характеры и промпты | SOUL + один стиль Маши + общие файлы | Каталог шаблонов, версии, ограниченное редактирование личности | M |
| Отношения | Числа в workspace, допускают авторскую правку | Серверный источник истины, стабильные interlocutor ID, запрет правки метрик игроком | M |
| LLM и секреты | Адаптеры готовы; Keychain/env/Codex login | Серверные credentials, модельная политика, streaming и бюджет вызовов | M |
| Голосовая сессия | Deepgram prerecorded + ElevenLabs WAV | Browser capture, streaming, interruption, управление соединением и задержками | L |
| Игровые команды | MCP, leases, whitelist, tracked receipts | Единый контракт действий и восстановления; подтверждение фактического результата | M |
| Identity и владение | Identity-сервис + назначения игрового сервера | Browser session и безопасная привязка аккаунт → герой → агент | M |
| Usage и стоимость | Ротируемая LLM-диагностика | Durable ledger всех запросов/аудио, тарифы, отчёты, лимиты, отдельные кредиты | L |
| AdminAgentHost | Отдельный серверный администратор | Сохранить отдельные полномочия; не использовать как персонажный runtime | S |
| Сквозная приёмка | Контрактные тесты и dialogue eval | Два пользователя, сбои/reconnect, WebGL-голос, p50/p95, сверка расходов | L |