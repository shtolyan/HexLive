# Состояние реализации §160 — 2026-09-05

## Реализовано локально

- Generic authored preset/spawn marker, сохранение физического NPC и migration
  v66–70 → v71; версия одежды теперь `CharacterPresetVersion`, не личная память.
- Generic MCP attachment/inbox/phases/turns/chunked WAV, wire v13, command
  arbitration; Deepgram token broker с авторизацией и ограничением выдач.
- Локальный AgentHost без HTTP bridge; память/workspace и transactional outbox;
  sleep без игрока, отмена платных запросов при уходе, независимый action lease.
- Generic UI Toolkit microphone/thought/relation/journal, прямой Deepgram STT,
  FMOD capture/VAD; runtime `.vis`, 2D reply и spatial Ambient, subtitles,
  snapshots VoiceCaptureDuck/AgentReplyFocus. FMOD banks пересобраны и скопированы
  в штатный StreamingAssets путь.
- `Tools/masha` lifecycle и бесплатный doctor; ключи не включены в код.

## Подтверждено тестами

- Полная симуляция: 1394 passed, 6 skipped. После выделения generic
  CharacterPresetVersion дополнительно пройдены 19 save/preset/spec тестов.
- Сервер: 150 passed (MCP/wire/TTL/STT/capability/idempotency).
- AgentHost: 14 passed, включая 31 секунду без игрока, пробуждение от текста,
  TTS text-only fallback, отмену хода и detach.
- Русский/Hexkufa/fallback `.vis` побайтно совпадают с offline golden fixtures.
- Unity presentation и новая test assembly компилируются без ошибок.
- `dotnet test HexLive.sln` завершается успешно, но генерируемая Unity solution
  не заменяет три отдельных тестовых проекта выше.
- Поиск credential-like литералов в изменённом контуре не обнаружил ключей.

## Не выполнено — не считать production-ready

- Unity EditMode/PlayMode execution и реальный микрофонный ход. Editor был
  открыт и загрузился без compile errors, но редакторский MCP остался Stdio,
  а доступный сервер ожидал HTTP. После проверки этот Editor закрыт.
  Автоматическое выполнение тестов не заявляется как пройденное.
- Production binary/handshake/JWT/STT acceptance, `masha on` на production и
  итоговое сравнение сохранённой внешности в игре.
- Deepgram env на VPS ещё не установлен. `Desktop/key` не читался и не удалён;
  его права ужесточены с 0644 до 0600. Удалять только после полного STT acceptance.
- Rollback старого бинарника без rollback мира: старый reader не читает v71.
  Нужен проверенный совместимый recovery release; до него safe fallback —
  выключить AgentHost/STT и оставить reader v71.

## Production и рабочее дерево

Read-only preflight: `hexlive.service` active, release
`11c13ff343ab2702a1e40acf02bc904f89931653`, существующий seed 12345,
10/10 живых NPC. Production не перезапускался, мир/assignments не менялись.

Runbook требует release из полного commit SHA и запрещает commit без отдельной
просьбы пользователя. Feature пока не закоммичена; в рабочем дереве есть
посторонние изменения inventory/wardrobe/skin и др. Их нельзя автоматически
включать в feature commit. Перед deploy отделить feature hunks и получить
разрешение на commit по правилам проекта.

Unity при старте предложил сохранить crash recovery scene; согласились на
сохранение. Новые `Assets/_Recovery/0 (4).unity` и `.meta` — резерв пользователя,
не feature-артефакты, не включать в release и не удалять.
