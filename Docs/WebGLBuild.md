# Сборка WebGL-клиента — инструкция для всех агентов

Для Claude, Codex и любого другого агента, на любой машине. Спека — `Spec/168.md`
(§168.6 звук, §168.10 хостинг, §168.11 рендер). Здесь только порядок действий.

## Главное правило

**WebGL-плеер собирается только через `HexLiveWebGLPlayerBuild.Build`**
(`Assets/Editor/WebGLPlayerBuild.cs`), а сразу после него запускается
`Tools/webgl_audio_fsb.py`. Других способов нет. Кнопка Build в окне Build
Settings, `BuildPipeline.BuildPlayer` из своего скрипта и «Build And Run» не
годятся: с 13.10.2026 такую сборку останавливает `HexLiveWebGLBuildGuard`
с `BuildFailedException`.

Почему: настройки, без которых веб-клиент трещит, тормозит и молчит, **в git
не лежат и не должны лежать**. `Assets/Plugins/FMOD/` целиком в `.gitignore`
(у каждой машины свой `FMODStudioSettings.asset`), а часть `PlayerSettings`
общая с десктопом. Поэтому скрипт ставит их сам на каждой сборке:

| Что | Значение | Зачем |
|---|---|---|
| Буфер микшера FMOD (WebGL) | 4 × 2048 сэмплов (~170 мс) | В браузере FMOD микширует на главном потоке, раз в кадр Unity. С буфером короче долгого кадра звук трещит |
| Платформа WebGL в настройках FMOD | включается (`AffirmProperties`) | Выключенная платформа молча берёт ВСЕ свойства у десктопа, и буфер не применяется (v3–v8 ушли так) |
| Обработчик старта FMOD | `WebAudioTuning.asset` | Частота 48 кГц, переключатели из URL (`fmodbuf=`, `fmodrate=`), строка `[FmodWeb]` в консоли |
| Оптимизация wasm | `RuntimeSpeed` (`-webgl-lto` → `RuntimeSpeedLTO`) | По умолчанию стоял `DiskSizeLTO` (-Oz) — медленный код |
| WebAssembly 2023 | вкл. | Нативные исключения и SIMD |
| Исключения | `FullWithoutStacktrace` | `Explicit` роняет страницу на NRE, `FullWithStacktrace` давал 509 JS-трамплинов на вызовы |
| Стек у Log/Warning | выкл. на время сборки, потом возвращается | Каждая строка лога со стеком съедала кадр, а с ним и микшер |
| Сжатие | Brotli без JS-фолбэка | Сервер отдаёт `Content-Encoding: br`, браузер компилирует wasm потоком |
| `web-audio-manifest.json` | пишется на время сборки | В браузере StreamingAssets — URL, обойти папку нельзя |
| Музыка mp3 → Ogg | на время сборки (ffmpeg + oggenc) | В веб-FMOD нет MP3-декодера |
| `devicePixelRatio = 1` | вписывается в `index.html` | Иначе на Retina рендер в 4 раза больше пикселей |

**Не «чини» это руками:** не коммить `FMODStudioSettings.asset`, не правь
буфер в окне FMOD Settings и не переключай `PlayerSettings` под веб в общем
проекте. Новая веб-настройка добавляется в `HexLiveWebGLPlayerBuild` и в эту
таблицу.

## Где собирать

- **macOS — полный путь.** FSB-кодер `hexfsb` собирается из macOS FMOD
  Engine SDK, музыку перекодируют `/opt/homebrew/bin/ffmpeg` и `oggenc`.
  Только на Маке звук можно закодировать заново.
- **Windows (MSI) — плеер без перекодирования звука.** Кодера на Windows нет.
  MSI собирает тем же `HexLiveWebGLPlayerBuild.Build`, но с флагом
  `-webgl-reuse-published-audio` (Codex, 12.10.2026, выпуск `msi-20261012-6ea38650b`).
  В этом режиме музыка не перекодируется, а в результат кладётся метка
  `audio-reuse-required.json`. Дерево FSB/`.vis` берётся из уже
  опубликованного релиза, и только после доказательства, что исходные
  `Sfx/**` и `Music` с тех пор не менялись (квитанция `audio-source-proof.json`).
  Проверка — `Tools/webgl_verify_audio_reuse.py` (команда ниже). Она проверяет
  все исходные `.ogg/.mp3/.wav/.vis` в `Sfx/**` и `Music`, включая LFS SHA-256;
  добавленные, удалённые и изменённые файлы закрывают путь переиспользования.
  Если звук менялся, нужна сборка на Маке. Проверка работает локально,
  не подключается к серверу, не копирует звук и не публикует архивы.
  Пакеты контента на MSI — `Tools/build_webgl_content_windows.py`, только по
  команде Толи.
- **Отдельный проект для сборки (Мак):** `~/hex-girls/webgl-build/HexLive`
  (worktree этого репо, уже импортирован под WebGL). Основной проект
  `/Volumes/ORICO/HexLive` под WebGL **не переключать**: `-buildTarget`
  в batchmode сохраняется, и Толя откроет редактор на WebGL с часовым
  переимпортом.
- **Один Unity на один проект.** Перед запуском проверь, что нет
  `~/hex-girls/webgl-build/HexLive/Temp/UnityLockfile` и что в
  `pgrep -fl "MacOS/Unity"` нет процесса с этим `-projectPath`. Чужой lock
  не удалять, чужую сборку не прерывать. Unity — только через CLI, без MCP
  (см. `CLAUDE.md`). Codex дополнительно соблюдает свой запрет из `CLAUDE.md`
  на запуск Unity при открытом редакторе Толи.

## Один раз на машину

1. Unity `6000.4.5f1` с модулем WebGLSupport (версия — `ProjectSettings/ProjectVersion.txt`).
2. `brew install ffmpeg vorbis-tools`.
3. Копия FMOD Engine SDK 2.03.x для macOS (лицензия Толи, в git не кладётся)
   и кодер:
   ```bash
   Tools/webgl/build_hexfsb.sh ~/hex-girls/webgl-build/fmod-2.03.15
   ```
   `Tools/webgl_audio_fsb.py` по умолчанию ищет
   `~/hex-girls/webgl-build/fmod-2.03.15/bin/hexfsb`; другой путь —
   `--hexfsb`.

## Сборка

1. **Код.** Собирается закоммиченный SHA. Перенеси исходники нужного коммита
   в проект сборки и запиши SHA; копии перезаписанных файлов клади рядом
   (`~/hex-girls/webgl-build/claude-sync-<sha>-backup/`, как делали раньше).
   Перед сборкой проверь без Unity:
   ```bash
   python3 Tools/webgl_compile_check.py
   ```
   На Windows тот же скрипт использует установленный WebGLSupport Unity
   и сгенерированные `.csproj` текущего checkout. Другой рабочий проект:
   `--unity-project <project> --checkout <source-checkout>`.
   Результат отдельно считает ошибки компиляции и RS0030, с колонками:
   две ссылки на запрещённый API в одной строке — два сообщения.
   Строгий запуск проверяет все три asmdef; `--report-only` служит только
   диагностике и не является проходом предрелизного гейта.
2. **Auto Refresh.** `CompileControl` держит его выключенным, и batchmode
   может выполнить `-executeMethod` на старых сборках. Делай как
   `Tools/build_release.py`: перед запуском временно поставь
   `kAutoRefreshMode`/`kAutoRefresh` = 1, а в `finally` верни прежние
   значения. Другой вариант — первый прогон без `-executeMethod`.
3. **Плеер.** Релизный по умолчанию. Это 15–35 минут, поэтому запускай в
   фоне и следи за логом:
   ```bash
   /Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity \
     -batchmode -quit -nographics -accept-apiupdate \
     -projectPath ~/hex-girls/webgl-build/HexLive -buildTarget WebGL \
     -executeMethod HexLiveWebGLPlayerBuild.Build \
     -webgl-output ~/hex-girls/webgl-build/player-<label>-<sha> \
     -logFile ~/hex-girls/webgl-build/unity-build-player-<label>-<sha>.log
   ```
   Флаги: `-webgl-development` — только по просьбе Толи;
   `-webgl-exceptions full|explicit` — для отладки; `-webgl-lto` — +1 час
   линковки ради нескольких процентов, только для финального выпуска по
   решению Толи; `-webgl-reuse-published-audio` — путь MSI, см. выше. Код
   выхода 0 — успех; `DisplayDialog` скрипт не показывает. Кроме плеера в
   результат кладётся `server-directory.json` — каталог серверов меню (§168.14).
4. **Звук в FSB — обязательно** (на MSI — проверенное переиспользование, см.
   «Где собирать»). Веб-FMOD читает только FSB; без этого шага
   голоса, музыка, лупы и фолбэк SFX в браузере молчат (`ERR_FORMAT`):
   ```bash
   python3 Tools/webgl_audio_fsb.py ~/hex-girls/webgl-build/player-<label>-<sha>
   ```
   Кэш кодера — `~/hex-girls/webgl-build/fsb-cache`, повторная сборка
   перекодирует только изменённое.

   **MSI: доказательство неизменности исходников опубликованного звука.**
   Нужны полный source SHA из квитанции именно аудиорелиза и его публичный
   `web-audio-manifest.json`, уже сохранённый локально. Не подставляйте
   текущий HEAD вместо этого SHA. До переноса опубликованных FSB/`.vis`:
   ```powershell
   python Tools/webgl_verify_audio_reuse.py --published-source-commit <audio-source-sha> `
     --manifest <local-published-web-audio-manifest.json> `
     --output Build/audio-source-proof.json
   ```
   При другом checkout добавьте `--checkout <source-checkout>`.
   Требуются exit0 и `passed:true`; при отказе файл результата перезаписывается
   с `passed:false`, старый PASS не сохраняется. Пути вне Sfx/Music и неоднозначные
   соответствия FSB → исходник отвергаются. Квитанция содержит только SHA,
   относительные пути и хеши, без ключей и аккаунтных данных. Она доказывает
   исходники, но не заменяет проверку хешей скопированных опубликованных FSB.

## Проверка: сборка правильная, только если всё ниже на месте

**Лог Unity** — все строки, в этом порядке:
```
[WebGLBuild] code optimization: RuntimeSpeed (…)
[WebGLBuild] FMOD WebGL DSP buffer: 4 x 2048, callback WebAudioTuning
[WebGLBuild] web music: N Ogg track(s) for the browser   (на MSI с reuse — 0, это норма)
[WebGLBuild] audio manifest: N files
[WebGLBuild] Succeeded: … 0 errors …
[WebGLBuild] devicePixelRatio capped at 1
```
Если хоть одной нет — сборку не публиковать.

**Вывод `webgl_audio_fsb.py`** — `[fsb] N sounds …`: число звуков порядка
2900 (на 12.10.2026 — 2872), не ноль.

**Браузер** (локально — `python3 Tools/webgl_serve.py <build-dir>`,
http://localhost:8090/):
- в консоли после первого клика есть
  `[FmodWeb] output …, mix 48000 Hz …, buffer 2048x4 …`;
- `?perf=1` в адресе включает профилировщик кадра (WebPerf).

## Упаковка и публикация

- В архив сайта не кладётся `*_BurstDebugInformation_DoNotShip`.
  Имя `webgl-site-<sha>.tar.gz`, рядом записывается SHA-256.
- **Публикация — только по прямой команде Толи, с названным сервером**
  (правила production в `CLAUDE.md`/`AGENTS.md`). Сингапур: архив →
  `/var/lib/hexlive-web/releases/<sha>`, затем атомарная замена symlink
  `/var/lib/hexlive-web/current`. Прежний релиз не удалять: это откат.
  Caddy — `Server/Caddy/singapore-webgl.caddy` (= prod).
- Плеер, сервер и контент — три разных слоя. Пересборка плеера не публикует
  контент и не перезапускает сервер. Совместимость проверяй по номеру
  протокола: старый сервер с новым клиентом не рукопожмётся. Кто публикует
  контент, записано в `RELEASE_COORDINATION_SVETA.md`.
- Квитанция выкладки — запись в `RELEASE_COORDINATION_SVETA.md`: исходный
  SHA, SHA-256 архива, старая и новая цель `current`, строки лога выше, что
  проверено в живом браузере и что нет.

## Ловушки, которые уже стреляли

- **Сборка «из окна» проходит успешно и уезжает без настроек:** буфер
  1024×4 вместо 4×2048, звук трещит на просадках FPS. Теперь её
  останавливает сторож, но проверка по логу всё равно обязательна.
- **«Поставил буфер в FMOD Settings, а в браузере 1024×4»** — платформа WebGL
  в настройках FMOD была выключена (`active: 0`) и наследовала десктоп.
  Скрипт её включает; результат проверяй строкой `[FmodWeb]`, а не файлом
  настроек.
- **Смотришь `FMODStudioSettings.asset` или `ProjectSettings.asset` в
  основном checkout и видишь «нет веб-настроек»** — так и должно быть, они
  ставятся при сборке. Ответ даёт лог сборки, а не эти файлы.
- **Без `webgl_audio_fsb.py` звука нет**, хотя Studio-банки грузятся и часть
  событий звучит. Тихие голоса — первый признак пропущенного шага.
- **LTO линкуется больше часа** (v5 был остановлен через 60+ минут).
