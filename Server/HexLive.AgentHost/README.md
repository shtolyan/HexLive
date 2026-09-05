# HexLive.AgentHost — локальный MCP-агент

Архитектура и wire v13: [§160](../../Spec/160.md). Это консольный .NET 8/9
MCP-клиент, без слушающего HTTP-сервера. Unity не подключается к AgentHost:
Unity использует `/watch`, AgentHost — `/mcp` игрового сервера. Редакторский
MCP for Unity к этому контуру отношения не имеет.

## Запуск Маши на macOS

Из корня проекта:

```sh
./Tools/masha doctor
./Tools/masha on
./Tools/masha status
./Tools/masha off
./Tools/masha logs
```

`on` запускает готовый AgentHost как launchd job `com.hexlive.agent-masha` и
находит ровно один живой `profileId=masha`. Отдельный локальный порт не нужен.
`off` посылает SIGINT непосредственно процессу AgentHost: запросы отменяются,
attachment и физический lease освобождаются. Аварийная страховка — серверный TTL.
Запуск без присутствующего назначенного игрока оставляет фазу `Sleeping`:
только бесплатный MCP heartbeat, без Grok и ElevenLabs. Появление игрока
будит агента автоматически; модельный heartbeat — 30 секунд, голос приоритетен.

После изменения исходников один раз выполнить `./Tools/masha build` при закрытом
Unity Editor. Обычные `on` и `doctor` никогда не запускают компиляцию, поэтому
безопасны для уже открытого редактора.

Не запускайте старый MollyBridge параллельно. При миграции старый env читается
один раз, нужные XAI/ElevenLabs-настройки копируются в новый защищённый env;
сам старый файл и workspace автоматически не удаляются.

## Конфигурация

Секреты находятся только в `~/.config/hexlive/agent-masha.env` (0600):

```sh
XAI_API_KEY=<локальный ключ xAI>
ELEVENLABS_API_KEY=<локальный ключ ElevenLabs>
# Необязательные настройки:
HEXLIVE_AGENT_MODEL=<модель>
HEXLIVE_MASHA_VOICE_ID=<голос>
MASHA_HOME='/путь/к/существующему/workspace'
```

`Tools/masha` читает отдельный MCP token из
`~/.config/hexlive/servers/62.146.235.120/mcp-token`. Ни один из этих файлов
не должен попадать в git, Unity assets или release-архив.

При непосредственном запуске DLL задаются `HEXLIVE_MCP_URL`, `HEXLIVE_MCP_TOKEN`,
`HEXLIVE_AGENT_PROFILE`, `HEXLIVE_AGENT_NAME`, `HEXLIVE_AGENT_STATE`, `MASHA_HOME`,
опционально `HEXLIVE_WORLD_ID`. `HEXLIVE_AGENT_FAKE=1` включает тестовые провайдеры.
HTTP разрешён только для loopback-разработки; production MCP должен быть HTTPS.

`doctor` проверяет конфигурацию, selector, наличие generic MCP-инструментов,
статус XAI key и доступ к выбранному голосу ElevenLabs через read-only API.
Модельные/TTS POST-запросы он не делает и provider response body не печатает.

## Память

Существующий workspace не заменяется: `SOUL.md`, `USER.md`, `MEMORY.md`,
`memory/worlds`, `memory/daily`, imports и `.state` продолжают использоваться.
Импорт iPhone и прежние эпизоды сохраняются. В модель попадают ограниченный
контекст и найденные фрагменты, а не весь архив. Последние 12 реплик живут
только в RAM. Transactional outbox хранит решения, не аудио и не транскрипты;
повтор commit не меняет bond/Social второй раз, переполненный outbox не
выкидывает неподтверждённые решения. Серверный Social не переносится в другой мир.

## Голос

Игрок: FMOD capture → Deepgram Nova-3 → финальный текст `/watch` → MCP inbox.
Агент: собственный TTS → WAV PCM16 mono 44100 → MCP чанки → `/watch` →
runtime MFCC/Viterbi `.vis` → FMOD и точный субтитр. Ошибка TTS оставляет текст.
Ответ игроку — 2D, Ambient — позиционный; оба идут через NpcSpeechDirector.

Deepgram master key нужен **только серверу**, env `HEXLIVE_DEEPGRAM_API_KEY`.
Клиент получает JWT на 60 секунд. OpenAI key и локальный bridge bearer не нужны.
Документация: [Deepgram temporary tokens](https://developers.deepgram.com/guides/fundamentals/token-based-authentication),
[XAI key check](https://docs.x.ai/developers/advanced-api-usage/mtls),
[ElevenLabs voice metadata](https://elevenlabs.io/docs/api-reference/voices/get).

## Проверка и rollout

Сначала `dotnet test` трёх проектов: `Tests/HexLive.Simulation.Tests`,
`Tests/HexLive.Server.Tests`, `Tests/HexLive.AgentHost.Tests`. Последний содержит
бесплатный fake-прогон sleep/wake/voice/cancel/detach и golden-тест runtime lip sync.
Golden fixtures содержат только эталонные виземы синтетического сигнала, не записи
голоса. Перегенерация калибровки: `Tools/export_runtime_lipsync.py`; эталонов:
`Tools/export_runtime_lipsync_fixtures.py`.

Production обновляется только по runbook `CLAUDE.md`: фиксированный release SHA,
backup текущего save/assignments/systemd, тот же каталог `/var/lib/hexlive`.
Сервер запускается с `--mcp --control --character-preset masha`; старый
`--companion masha` — alias на один релиз. Мир не пересоздаётся.

Wire v13 и save v71 требуют согласованного обновления. Старый сервер не умеет
читать v71: **нельзя объявлять откат старого бинарника безопасным без проверки
совместимости**. До такого rollback нужен recovery-бинарник, читающий новый save;
аварийный способ без потери мира — выключить AgentHost/STT и оставить совместимый
сервер. Исходный Deepgram-файл удаляется только после успешных JWT/STT и handshake
проверок на production, не при одной лишь установке env.
