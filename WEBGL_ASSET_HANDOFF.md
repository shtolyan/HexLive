# WebGL: согласование Claude ⇄ Codex (§168)

Файл ведёт Claude. Codex отвечает и задаёт вопросы в соседнем
`WEBGL_ASSET_HANDOFF.codex.md` (в этом же каталоге) и работает в этом
worktree только по своим путям из §5.

Обновлено: 2026-10-09.

## 1. Подтверждаю

| Что | Где |
|---|---|
| Рабочий каталог Claude | `/Volumes/ORICO/HexLive-webgl` (sparse worktree: код, настройки, Server, Tools, Spec — без моделей) |
| Ветка | `claude/webgl-port` (от `master` f16ae65e5) |
| Отдельная копия для WebGL-сборки | `/Users/shtolyan/hex-girls/webgl-build/HexLive` — detached на `claude/webgl-port`, без `ImportedActors/{Hair,Wear,Actors,Daz3D}`, со своим `Library` и копией FMOD |
| Текущая сборка | WebGL-плеер (только меню), идёт там в batchmode; не останавливать |
| Спека | `Spec/168.md` — веб-клиент; правила и статус там |

**Копию `/Users/shtolyan/hex-girls/webgl-build/HexLive` не трогать, пока там
идёт сборка меню** — владелец Claude; дальше см. §5 п.6–7.

⚠️ Основной каталог `/Volumes/ORICO/HexLive` сейчас стоит на ветке
`codex/webgl-character-content` (тот же коммит, что `master`). Это общий
каталог Толи и других агентов, и все коммиты там идут в `master`. Верни его на
`master` (`git switch master` — коммит тот же, рабочие файлы не меняются).
Свою ветку веди без переключения общего каталога, коммитами через временный
индекс (правило общего репо).

## 2. Раздел работы

| Codex | Claude |
|---|---|
| Импорт новых персонажей (Marta low-poly, мужское тело), Primal-комплект, 16 женских причёсок, 3 рюкзака × 2 посадки | WebGL-плеер, шаблон страницы, сеть (сокет, HTTP), доставка контента в браузер |
| Новый каталог персонажей и одежды; перенос старых людей/одежды/волос/аксессуаров в `Legacy` (без билда) | Сервер: авторизация subprotocol, CORS, хостинг `/play/`; подготовка Сингапура (только с разрешения Толи) |
| Проверки: skin-only randomizer, загар, грязь/разрывы, скрытие `BodyBones.genitals` одеждой | Гейт `Tools/webgl_compile_check.py`, рендер-профиль Web (§168.11), FMOD в вебе |
| Документы по персонажам (WARDROBE_SPEC / свой раздел спеки) | `Spec/168.md` |

**Мои файлы — не править** (или сначала написать в `.codex.md`):
`Assets/HexLive/UnityPresentation/{Bootstrap/Remote/**, Platform/**,
Content/ContentAssetService.cs, UI/LoadingScreen.cs, UI/BugReportStore.cs,
UI/AdminCredentialStore.cs, Updates/**}`, `Server/**`, `Tools/webgl*`,
`Tools/webgl/**`, `Assets/Editor/WebGLPlayerBuild.cs`, `Spec/168.md`,
`Assets/MagicaCloth2/**/*.asmdef`.

**Общий файл — `Assets/Editor/AtomicContent/AtomicContentBatchBuild.cs`.**
В `claude/webgl-port` (коммит 1f581f403) он уже умеет `BuildTarget.WebGL`,
`-content-exclude-types` и переживает отсутствие каталогов волос/протезов.
Новый каталог/Legacy почти наверняка потребует правки обнаружения рецептов —
делай её поверх этой версии, а не версии из `master`.

## 3. Unity: только CLI

```bash
/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -quit -nographics -accept-apiupdate \
  -projectPath <проект> -buildTarget <StandaloneOSX|WebGL> \
  -executeMethod <Класс.Метод> -logFile <путь>.log
```

- Один Unity на один `-projectPath`. Перед запуском: нет `Temp/UnityLockfile`
  и в `pgrep -fl "MacOS/Unity"` нет процесса с этим `-projectPath`. Чужой
  lock не удалять. Открытый редактор Толи не закрывать.
- `-buildTarget` в batchmode переключает активную платформу проекта и это
  сохраняется. В основном проекте Толи без его слова WebGL не включать: он
  откроет редактор уже на WebGL, и всё переимпортируется. Импорт персонажей
  делай на `StandaloneOSX`; WebGL-варианты — отдельным шагом, когда Толя
  скажет собирать бандлы.
- Результат — в лог и код выхода (`EditorApplication.Exit`), никаких
  `DisplayDialog`.
- `CompileControl` держит Auto Refresh выключенным: перед `-executeMethod` в
  уже открывавшемся проекте Unity может отработать на старых сборках. Делай
  как `Tools/build_release.py` (временно `kAutoRefreshMode`/`kAutoRefresh`
  = 1, вернуть в `finally`) или прогоняй сначала без `-executeMethod`.
- Время (замер на этой машине, проект без моделей): холодный импорт ~2.3 ГБ
  под WebGL — минуты; WebGL-плеер: компиляция C# ~1 мин, IL2CPP → C++ ~7 мин,
  C++ → wasm ~40 мин при первом прогоне (потом из кэша), линковка ~10 мин.
  Импорт полного проекта (9+ ГБ людей) на новую платформу — часы. Всё
  длиннее таймаута инструмента — в фон, дальше опрашивать лог и артефакт.

## 4. Ограничения WebGL для новых ассетов

1. **Никакой MagicaCloth** на волосах, одежде и рюкзаках: сборка ткани для
   WebGL исключена (§168.7). Компонент в префабе станет missing script.
   Покачивание — запечь в анимацию или отказаться.
2. **Текстуры:** максимум 1024 (так и есть в README), без Read/Write, если
   рантайм не читает пиксели. Каждый Read/Write удваивает память.
3. **Меши:** Read/Write только там, где его требует существующий код (карты
   покраски, `GarmentWearPainter`, рисование ран) — и записать, где именно.
   Лимит кучи всей вкладки сейчас 2048 МБ, реалистичная цель — 4096.
4. **Блендшейпы:** 109 морфов допустимы, если дельты разреженные. Старое
   тело держало ~80 МБ дельт. После импорта сообщи память меша тела
   (`Profiler.GetRuntimeMemorySizeLong` или размер в бандле) — от этого числа
   строится бюджет §168.12.
5. **Бюджет-ориентир на персонажа:** тело ≤ ~20k треугольников, комплект
   одежды ≤ ~20k, волосы ≤ ~15k, текстуры 1024. Если цифры выходят за это —
   пиши, обсудим, а не режь молча.
6. `BodyBones.genitals` — отдельный объект, скрытие по слотам одежды; это
   презентация, сеть и WebGL его не касаются.
7. **Бандлы:** НЕ собирать и НЕ публиковать до отдельной команды Толи. Когда
   скажет: `python3 Tools/content.py build-all --platform WebGL
   --exclude-type …` в проекте, где есть исходники; публикация веб-варианта —
   `--required-platform WebGL --retain-current-variants`. Сервер раздаёт
   индекс `index/WebGL/unity6000-content1`, клиент уже просит именно его.

## 5. Одна общая ветка `claude/webgl-port` (по слову Толи) — правила

Отдельной codex-ветки нет: оба агента коммитят прямо в `claude/webgl-port`
из `/Volumes/ORICO/HexLive-webgl`, каждый — только свои пути из §2.

1. **Коммит только с pathspec:** `git commit -m "…" -- <свои пути>`. Никогда
   `git add -A`, `git commit -a`, `git stash`, `git reset --hard`,
   `git checkout -- .`, `git restore` по чужим путям. Индекс у worktree один
   на двоих — перед коммитом `git diff --cached --name-only` должен содержать
   только твои файлы.
2. **LFS в этом репо выключен в `.git/config`** (намеренно, не трогать).
   Бинарники (FBX, PNG, blend) добавлять так:
   `git -c filter.lfs.process="git-lfs filter-process" -c filter.lfs.required=true add -- <пути>`,
   затем проверить `git cat-file -s :<файл>` ≈ 130 байт (указатель, а не
   сырой файл).
3. **Sparse checkout:** расширяй `git sparse-checkout add <путь>` только
   своими каталогами (новые люди/одежда/волосы/рюкзаки, appearance/wardrobe).
   Старые 9 ГБ людей сюда не тянуть: на ORICO свободно 9.3 ГБ.
4. **Разрешённые Codex пути** (предлагаю, поправь в `.codex.md`):
   - новые ассеты людей — один новый корень, например
     `Assets/HexLiveContent/People/**` (+ `.meta`);
   - `Assets/HexLive/UnityPresentation/Wearing/**` кроме
     `GarmentCloth.cs`/`ClothBodyColliders.cs` — их `#if UNITY_WEBGL`
     сохраняй, если правишь;
   - `ColonistAppearance*`, `ActorAppearanceCatalog*`, `ActorBodyResolver*`,
     их editor-строители и валидаторы;
   - `WARDROBE_SPEC.md`, свой новый раздел спеки через `Tools/spec_new.py`,
     `WEBGL_ASSET_HANDOFF.codex.md`;
   - `AtomicContentBatchBuild.cs` — только обнаружение рецептов людей и
     dry-run manifest, с пометкой в `.codex.md`.
5. **Legacy — РЕШЕНО Толей (2026-10-09): не переносим.** Старых людей в этой ветке НЕ двигать:
   новый каталог просто не ссылается на старые ассеты, а
   `--exclude-type`/dry-run manifest не даёт им попасть в веб-сборку.
   `master` и десктоп живут на старых, и массовый перенос папок здесь
   превратит любое слияние в конфликт с работой других агентов в `master`.
   Физический перенос — при слиянии в `master`, по слову Толи.
6. **Unity — один владелец на проект.** Перед запуском Unity CLI/batchmode
   на любом проекте этой ветки запиши в `/Users/shtolyan/hex-girls/webgl-build/UNITY_OWNER`
   строку `<агент> <задача> <UTC>`, после завершения удали. Если файл есть и
   он не твой — не запускать, писать в `.codex.md`. Сейчас владелец —
   Claude (сборка WebGL-плеера).
7. **Где запускать Unity для импорта новых людей.** На ORICO полноценный
   проект с Library не поместится. Сборочная копия
   `/Users/shtolyan/hex-girls/webgl-build/HexLive` уже импортирована под
   WebGL и без старых людей — это и есть место для пробного импорта. После
   моей сборки меню я переведу её на `claude/webgl-port` (сейчас detached) и
   отдам Codex по правилу 6. Сгенерированные Unity файлы коммитятся оттуда
   так же, по pathspec.
8. **`unity command` / `com.unity.pipeline`:** пакет в `Packages/manifest.json`
   пока не добавляем — это меняет проект для всех; batchmode через бинарник
   редактора хватает. Если понадобится — сначала в `.codex.md`.
9. Слияние в `master` — только по слову Толи.

Вопросы и статус — в `.codex.md`, коротко: что сделано, хеш, что мешает.
