# Звонки, долларовый баланс и визуальная речь — #443–447

Поручение Толи 2026-10-12: 15 секунд молчания пользователя; время ответа
персонажа исключено. Стартовые $5 каждому **новому** аккаунту. Повторный вход
и подключение нового провайдера к той же Firebase identity не выдают новые деньги.
Существующие аккаунты не получают кредит задним числом.

## Аудит фактического расхода (read-only, Singapore)

Журнал: 113 LLM попыток, 118875 input tokens, 4426 output tokens; 6 голосовых
сессий, 587 секунд. В старом журнале стоимость всех 119 записей unknown:
VOICEBRAIN_TARIFFS не был задан. Это не означает бесплатные вызовы.

Сверка этих 6 conversationId непосредственно с ElevenLabs: все done,
подтверждённая сумма **$0.77762126314538437**, длительность 587 секунд.
Печатаются только агрегаты, ни реплики, ни ключи, ни account identity.
Результат пока не записывается задним числом в production journal.

`metadata.cost` — кредиты провайдера, `metadata.cost_fiat` — USD. Основание:
https://elevenlabs.io/docs/changelog/2026/7/6

LLM использует реальные input/output/cached tokens с версионным стандартным
тарифом из JSON (2026-10-12: Luna $0.20/$0.02/$1.20 за миллион), source:
https://developers.openai.com/api/docs/models/gpt-5.6-luna
Такие суммы обозначены estimate, не provider invoice. Неизвестные единицы
остаются pending, не переводятся в ноль. Локальные world/MCP команды отдельной
выдуманной комиссии не имеют; дополнительные model/tool-result passes входят
через реальные usage events.

## Реализация и ограничения

#443: единое завершение WebRTC и billing, VAD + transcript, 15 секунд.
Node lifecycle9/9; source commit490b6dc51119102a3d95c7d6c8aae92230155af0.
#444: durable grant в account store, owner-only balance API, latest EventId/revision
projection, no rounding in debits, explicit estimate/pending. Перезапуск/провайдер
не повторяют grant. Новые платные session/brain calls запрещены при balance<=0.
UI beside language renders dollars to4 decimal places with pending/estimate marker.
Final EL billing checks opaque session userId; повтор не списывает второй раз.
#445: существующий NpcSpeechDirector резервируется WebRTC на весь звонок;
обычные/Studio голоса не перекрывают, старые cues после звонка не проигрываются.
#446: существующая calibrated G2P map + provider character timing chunks →
те же 14 Daz visemes; reset при interruption/end. SDK1.25.0 source verified:
WebRTC forwards alignment without replaying audio. Agent client_events includes
`audio`/`interruption`; никаких provider settings не меняли.
#447: единая scarlet palette для particle drips/hit splashes/fresh ground/water;
particle gradients сохраняют alpha fade, ground clones не меняют shared assets.

WebRTC не отдаёт FMOD playback position: timing is driven by received alignment
and SDK speaking transition; network/audio skew needs real microphone/portrait QA.
Месячный usage journal нужен для сверки неизвестных/отменённых LLM попыток.
Закрытие вкладки/краш без доставленного session/end требует recovery reconciliation;
данная работа не заменяет provider webhook reconciliation. Долг живого звонка
может проявиться после финализации провайдера; balance не является предварительной
резервацией максимальной стоимости длительного звонка.

Новые изменения production не опубликованы; предыдущий SGrelease1214a639a
остаётся активным, Player6ea и content564. Для UI/визем нужен новый Player;
для timer/wallet новый server. New York не затрагивался.


## Итоговая проверка и handoff

- Server full **479/479**, VoiceBrain **8/8**, browser lifecycle **9/9**,
  Unity PlayMode **16/16**, 0 failures. Prototype host builds successfully;
  backend rebuilt with the final embedded module. Только локальные fake-provider
  tests; никаких платных LLM/TTS test calls не делали.
- Native D3D11: `visual/mouth-closed.png`, `mouth-speaking-aa.png`,
  `mouth-interrupted.png`, `red-drips.png`, `balance-fixture.png` под
  `Build/MSIRelease/20261012-voice-budget`. AA shape weight80;
  исходный и после-перебивания кадры имеют одинаковый SHA256.
  Balance кадр содержит **тестовую** сумму, не баланс реального аккаунта.
- LLM audit: 110 complete counters оценены в **$0.02875086** по стандартному
  тарифу, cached tokens1863; 3 attempts остаются unknown. Voice provider total
  **$0.77762126314538437** confirmed. Суммарно известная/оценённая часть
  $0.80637212314538437, дополнительно 3 неизвестных model attempts.
- Provider agent сейчас не публикует vad_score. SDK mic-volume fallback5Hz
  защищает длинную речь до final transcript; тихий фон ниже0.04 не продлевает,
  громкий фон остаётся для реального телефонного QA. Error402 не маскируется
  синхронным ended event. Provider config не менялась.
- Сгенерированный `source-receipt.json` фиксирует реальные source/image hashes.
  Это **не** deployment receipt. Следующий релиз требует отдельной команды
  обновить конкретно Singapore server+Player; content564 подходит к этим
  code-only изменениям, новых wound-map/config объектов здесь нет.

Commits:
443:490b6dc51119102a3d95c7d6c8aae92230155af0 +87e140d1ef8a3864d03df9d8a347284f6e77efb5
444:6f304229cd2cb5843ca0a7ac9a6aed2e75895491
445:2e45ef4f1027d32eecd720a4055622a541bfde99
446:2be9ae26e2bd70c9f2a9ac6e73d9ed285fdde77b
447:a5ac0b582f7066d12f26cd714bb7879f8d9c5140

Overlapping files for Mac integrator review:
`CharacterPanel.cs`, `CharacterPanel.AgentVoice.cs`,
`CharacterPanel.ServerVoice.cs`, `NpcActorView.cs`, `NpcVoiceLipSync.cs`,
`RuntimeVoiceAlignment.cs`, `WebVoiceAgent.cs`, `Program.cs`, `VoiceEndpoints.cs`,
`AccountStore.cs`. Не заменять их слепо; focused commits are on existing MSI branch.
New account grant deliberately does not retro-credit Tolya's existing account.
Перед включением wallet gate это учитывать; автоматической выдачи старым
аккаунтам, top-up/payment UI или банковской резервации в этой работе нет.

Capture Editor exited normally after writing images. Existing pipeline/shutdown
warnings include Native leak reports and a shutdown NullReference; no crash or
ongoing import is claimed. Full scene/real phone QA is still required.
