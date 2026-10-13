## CURRENT OVERRIDE — immediate work authorization from Tolya, 2026-10-11
This section supersedes ALL blanket read-only/base-pending instructions below. Tolya explicitly directs MSI to stop waiting and fix hair now. Sveta removes her blanket coordination hold.

- Existing visual-bugs worker: begin HV-001 immediately in the existing isolated branch/worktree at 820f7ca7b674dc6e5c8d2df9293f4258c8e3084a. This base is approved for isolated development, NOT for replacing Mac HEAD or publishing.
- Start Unity audit/fixes of hair/scalp materials, texture retention, transparency and face orientation using prior Git fixes as evidence. Verify LFS resources and all affected hairstyles; save before/after evidence and focused tests.
- Do not wait for bridge redesign, Flashback TLS, or general base agreement. Use local Git/reference assets where available.
- Known Mac overlap files: PortraitStage.cs, CharacterPanel.cs, NpcActorView.cs, NpcFaceAnimator.cs, HexWorldRenderer.cs. Do not wholesale replace these on Mac. If a hair fix needs one, isolate that diff and report it for integration review; this is not a reason to stop other work.
- After hair, continue nonoverlapping materials/normal-map work under the existing five-bug authorization. Coordinate overlapping runtime changes individually, not by freezing the entire branch.
- One existing worker only; no new developer, no WebGL build, no deployment. Preserve dirty user files. Send started ACK and actual progress through tell_sveta.sh.
- Mac remains final integrator and WebGL builder. Cherry-pick/review focused commits onto its current HEAD; keep its audio/performance fixes.

# MSI Codex — пять визуальных багов HexLive

Прямое поручение Толи от 2026-10-11: существующий Codex на MSI берёт HV-001…HV-005, проводит аудит и исправления в установленном Unity. WebGL Build Support не устанавливать, WebGL не собирать, production не публиковать. Финальная сборка и публикация — только через текущего владельца на Маке. Света координирует и принимает отчёты.

## Получить полный трекер
Прочитать на VPS `/home/openclaw/.openclaw/workspace/tasks/hexlife-visual-bugs-20261011.md` через уже проверенный SSH и сохранить копию в рабочей документации. Это полные требования и критерии приёмки, не заменять их этой выжимкой.

1. HV-001: аудит всех причёсок, прозрачность скальпа, стороны рендера; изучить прошлые Git-фиксы волос.
2. HV-002: восстановить emoji инвентаря в WebGL независимо от ОС. Желаемый вид — macOS. Проверить источник ресурсов и возможность распространения, не подменять стиль без отчёта.
3. HV-003: восстановить normal maps тел/одежды и прежнюю силу рельефа по эталонным материалам.
4. HV-004: корректные зоны бинтов для новых моделей, сравнение со старым персонажем; вернуть normal maps.
5. HV-005: измерить частоту портрета снизу слева. Намеренную оптимизацию документировать, реальный баг исправить. Это может потребовать runtime-кода, не только ассетов.

## Начало и изоляция
- Сразу пришли ACK Свете через уже работающий `tell_sveta.sh`: принятие всех пяти задач, текущие HEAD/ветка, предлагаемый base SHA и план.
- Прочитай AGENTS.md проекта. Сохрани изменения HexLiveReleaseBuilder.cs, ProjectSettings.asset и TestResults/. Не сбрасывай и не коммить чужое.
- Не работать прямо в master. Проверить origin и доступный актуальный remote HEAD; создать отдельный чистый worktree/ветку `sveta/msi-visual-bugs-20261011`. Зафиксировать точный base SHA, сверить с Маком через Свету. Не считать старый 91300fba автоматически правильной базой.
- До согласования базы можно делать read-only аудит и изучать историю; изменения выполнять в изоляции. Проверить Git LFS и фактическое наличие исходных моделей/текстур; WebGL-бандлы не подменять Windows-бандлами.
- Unity 6000.4.5f1 уже установлен. На MSI только один процесс Unity, владеющий данным worktree. Не менять глобальные настройки проекта ради платформы без необходимости.

## Работа и передача
- Исправлять последовательно или согласованными непересекающимися частями; HV-003/004 связаны. Отчёт о причине и скриншоты до/после для каждого пункта.
- Не ограничиваться настройками ассетов, если доказанная причина в импортере/рендере/runtime. Такие исправления допустимы в рамках пяти багов, с явным списком файлов для Мак-интегратора.
- Старые модели — только сравнительный эталон, не возвращать старый контент в production.
- Каждую проверенную часть передать Маку отдельным commit/patch и handoff: base/fix SHA, файлы, Unity-проверки, изображения, зависимости и оставшийся WebGL QA. Пуш только рабочей ветки, не master; не сливать и не публиковать самостоятельно.
- По готовности локально статус `ready_for_live_QA`, не `verified`: финальная проверка через Mac WebGL и опубликованную игру обязательна.
- Отчёты Свете: ACK, найденные блокеры, завершённые пункты и финальный handoff. Не выводить ключи, токены, пароли. Если обратный канал команд пока только через этот файл — явно сообщить и повторно читать файл при согласованных проверках; не утверждать постоянное наблюдение без работающего механизма.

## Статус отправителя
ACK MSI получен: HV-001…HV-005 приняты для текущего релиза; до согласования базы только read-only аудит. Пользователь разрешил работу; повторного согласия на этот объём не требуется.

## Coordination update — 2026-10-11, after MSI ACK
MSI proposed base: 820f7ca7b674dc6e5c8d2df9293f4258c8e3084a. Mac checked live: HEAD 4b34c09a6a3f913b29f602b77e48fc0003419cff; proposed base is an ancestor, but five local commits alter overlapping files, including UI/PortraitStage.cs, UI/CharacterPanel.cs, Wearing/NpcActorView.cs, Wearing/NpcFaceAnimator.cs and Rendering/HexWorldRenderer.cs under Assets/HexLive/UnityPresentation. Therefore base agreement remains PENDING, not approved simply because remote master matches. Continue read-only inventory/history audit. Do not overwrite these newer files. Mac integrator is asked to confirm a shared published base or explicitly accept cherry-picking fixes from 820f7ca7 with overlap review. No verified Flashback issue mapping available; HV-001…005 remain authoritative IDs. Reply delivery is via this file; no automatic consumption asserted.
