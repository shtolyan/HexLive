# Сборка под iOS — разведка

Статус: **исследование, ничего не менялось.** Ни одной правки в код, настройки
проекта или контент не внесено. Документ отвечает на вопрос «можно ли собрать
билд на iOS и что для этого нужно», с числами, снятыми с текущего дерева
(коммит на момент замера — см. `git log -1`).

Все размеры ниже — **истинные**, с раскрытыми LFS-указателями (в рабочей копии
многие файлы лежат указателями по 134 байта, и `du` покажет ерунду).

---

## Короткий ответ

Собрать можно, но это не «переключить Build Target и нажать Build». Технических
запретов нет — весь стек (Unity 6000.4.5f1, URP 17.4, Input System, Addressables 4,
Burst, MagicaCloth2, FinalIK, uLipSync, I2) поддерживает iOS. Мешают три вещи, и
только одна из них — программирование:

| | Что | Тип |
|---|---|---|
| **1** | У FMOD-проекта нет платформы iOS — банки собраны только под Desktop | конфигурация + пересборка банков |
| **2** | Контент живёт ПАПКОЙ РЯДОМ С .app — на iOS такого места не существует | архитектура загрузки |
| **3** | Бюджет ассетов и памяти: тело — 367 МБ на меш, ~10 ГБ исходников | **настоящий блокер, требует мобильного тира контента** |

Плюс тач-ввод, которого нет вообще (0 упоминаний `Touchscreen` в проекте), но
это как раз самая дешёвая часть — вся мышь и клавиатура сидят в трёх файлах.

Порядок работ и оценка — в конце.

---

## 1. Что уже готово и работать будет

Это стоит зафиксировать, потому что список приятно длинный:

- **Unity 6000.4.5f1 + URP 17.4** — полноценная поддержка iOS/Metal.
- **Мобильный тир качества уже смаплен.** `QualitySettings`: `iPhone: 0`, а
  уровень 0 — это `Mobile` (`shadowDistance: 40`, `pixelLightCount: 2`,
  `antiAliasing: 0`). Рядом лежат `Assets/Settings/Mobile_RPAsset.asset` и
  `Mobile_Renderer.asset` из шаблона URP.
- **Ввод — только новая система.** `activeInputHandler: 1`. Легаси
  `Input.GetKey/GetMouse` встречается ровно в двух файлах, и оба — примеры
  MagicaCloth2 (`Example (Can be deleted)`), то есть в игре его нет.
- **Нативных десктопных плагинов в репозитории нет.** Поиск `.bundle/.dylib/.a/
  .so/.framework` по `Assets` не находит ничего (кроме содержимого MagicaCloth2).
  Единственный нативный код — FMOD, а он в своём дистрибутиве возит статические
  библиотеки под iOS.
- **Всё middleware — iOS-совместимое:** MagicaCloth2 (Burst/Jobs), FinalIK
  (чистый C#), uLipSync (Burst, локальный пакет `Packages/com.hecomi.ulipsync`),
  I2 Localization, Addressables 4.0.0, Burst 1.8.29, Collections 6.4.0.
- **Пользовательские файлы уже пишутся в `persistentDataPath`:** `SaveGame`
  (`hexlive_save.dat`, `hexlive_new_seed.txt`), `GameHistoryLog`, и
  `BugReportStore` в последнем шаге фолбэка. То есть песочница iOS их не сломает.
- **`StreamingAssets` на iOS — настоящая читаемая папка внутри бандла** (в
  отличие от Android, где это архив). Значит `FmodSfx` может и дальше сканировать
  `Sfx/Voices/**` через `Directory`/`File` и стримить музыку из
  `StreamingAssets/HexLive/Music` через Core API — §67.6/§70 переносятся как есть,
  без единой правки. Это большая удача, её легко было бы не иметь.
- **Мобильная ветка уже застолблена в коде:**
  `PrototypeRuntimeBootstrap.cs:194` уже выбирает `LunarRuntimeConsoleProvider`
  под `(UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR`, а `DesktopRuntimeConsole` —
  под десктоп. Сам провайдер (`LunarRuntimeConsoleProvider.cs`) написан на
  рефлексии, чтобы проект компилировался до импорта ассета Lunar.
- **В `ProjectSettings.asset` iOS-ключи уже разумны:** `iOSTargetOSVersionString:
  15.0`, `targetDevice: 2` (iPhone + iPad), `iOSAutomaticallyDetectAndAddCapabilities: 1`.

---

## 2. Блокер №1 — FMOD не знает про iOS

`FMODStudio/HexLive/Metadata/Platform/` содержит **ровно одну платформу**:

```
{c9f35dc9-...}.xml → name: Desktop, subDirectory: Desktop
```

Соответственно и банки собраны только под Desktop:
`Assets/StreamingAssets/Master.bank` (20.5 МБ), `Master.strings.bank`,
`Assets/StreamingAssets/FMODBanks/`. iOS-банков не существует.

Что надо сделать:

1. В FMOD Studio добавить платформу **iOS**, назначить ей энкодинг (обычно
   FADPCM для sfx, Vorbis для музыки) и собрать банки в `Build/iOS/`.
2. В `FMOD for Unity` в настройках платформы iOS указать путь к банкам и
   поставить `BankLoadType: All`, как сейчас у Desktop.
3. Скачать интеграцию заново на билд-машину — `Assets/Plugins/FMOD/` в
   `.gitignore` (297 МБ, §67.12), а iOS-библиотеки лежат именно там. Версия
   обязана быть **2.03.14**, ровно как у FMOD Studio.
4. Проверить аудио-сессию iOS: прерывания (звонок, будильник), режим
   silent-switch. FMOD это умеет, но поведение надо выбрать осознанно.

Чего делать **не** надо: трогать `populate_events.js` / `sync_voices.js`. Это
вопрос платформы и сборки банков, а не структуры событий. И голоса, как обычно,
вообще мимо Studio — `FmodSfx.EventPathFor` принудительно уводит всё, что
начинается на `voice_`, на Core API поверх файла из `StreamingAssets`.

---

## 3. Блокер №2 — внешний контент негде положить

Сейчас гардероб грузится Addressables-каталогом из папки, лежащей **рядом с
приложением**:

```csharp
// ExternalContentPath.cs:22
#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
    // dataPath = <build>/Game.app/Contents → два уровня вверх
    return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", FolderName));
#else
    return Path.GetFullPath(Path.Combine(Application.dataPath, "..", FolderName));
#endif
```

и профиль Addressables повторяет то же:

```
HexLiveContent.LoadPath = {UnityEngine.Application.dataPath}/../HexLiveContent/[BuildTarget]
```

**На iOS папки рядом с бандлом нет.** Приложение — песочница, бандл только для
чтения, никакого «положим контент в соседнюю директорию, её разделят несколько
билдов» (а это, судя по комментарию, и было целью такого решения).

Два пути:

- **A. Каталог внутрь IPA.** iOS-профиль Addressables с локальными путями
  (`Addressables.BuildPath` / `Addressables.RuntimePath` → StreamingAssets).
  Просто, работает офлайн, но весь вес контента ложится в IPA (см. блокер №3).
- **B. Удалённый каталог + докачка в `persistentDataPath` при первом запуске.**
  Обязателен, если контент не влезает в лимиты App Store. Дороже: нужен хостинг,
  прогресс-экран, обработка обрыва, версионирование каталога.

В любом случае правится:

- `ExternalContentPath` — ветка `UNITY_IOS` (и `InstallAddressablesPath`, который
  сейчас переписывает legacy-корень только под `UNITY_STANDALONE_OSX`);
- профиль Addressables — отдельные значения для `[BuildTarget]` = iOS;
- `Assets/Editor/Addressables/HexLiveIconPatch.cs:49` — жёсткая проверка
  `activeBuildTarget != BuildTarget.StandaloneOSX`;
- валидация каталога в `Tools/build_release.py` (проверяет OSX-каталог на
  наличие wear/hair/icons/протезов перед подписью).

---

## 4. Блокер №3 — вес и память. Здесь настоящая работа

Истинные размеры исходников (LFS раскрыт):

```
  3574 МБ  Assets/ImportedActors/Wear      (686 .mesh = 2250 МБ, 548 .jpg = 919 МБ, 80 .png = 377 МБ)
  3457 МБ  Assets/ImportedActors/Hair      (550 .png = 2740 МБ, 13 .mesh = 324 МБ)
  1620 МБ  Assets/ImportedActors (прочее)
   282 МБ  Assets/HexLive
   261 МБ  Assets/StreamingAssets/HexLive/Sfx  (1093 LFS-файла)
   161 МБ  Assets/RVFX
 ─────────
 10142 МБ  ИТОГО по Assets
```

Записей в группах Addressables: **HexLive.Hair — 1771**, **HexLive.Wear — 864**,
Icons 22, Prosthetics 9. В `catalog_items.json` — 685 предметов.

Самые тяжёлые отдельные объекты:

| Меш | Размер |
|---|---|
| `Actors/Molly/MollyMesh.mesh` | **367.7 МБ** |
| `Actors/Jolly/Jolly.mesh` | **367.1 МБ** |
| `Hair/BendineHair/Meshes/BendineHair.mesh` | 57.4 МБ |
| `Hair/Bob3Hair/Meshes/Bob3Hair.mesh` | 40.2 МБ |
| `Wear/PrimalDress/Meshes/Molly.mesh` | 34.0 МБ |
| медиана меша одежды (686 шт.) | 1.56 МБ |

(spec §, строка 6874, называет `MollyMesh.mesh` «385 MB standalone» — сходится.)

**Почему это блокер, а не «оптимизируем потом».**

- Тело — плотный DAZ Genesis 3 с полным набором морфов; `NpcActorView.cs:1363`
  ориентируется на `blendShapeCount > 50`. Меш грузится целиком в момент спавна
  первой колонистки. Лимит памяти приложения на iOS — примерно 1.4 ГБ на
  устройствах с 3 ГБ ОЗУ и 2–3 ГБ на 6–8 ГБ. Один такой меш съедает четверть
  бюджета до того, как отрисован первый кадр.
- Поверх этого `SkinTexturePainter` создаёт рендер-текстуры **на каждую NPC, на
  каждый слот материала** (`_materials = body.materials; // instantiate once, per NPC`):
  альбедо до `MaxRenderTextureSize = 2048` ARGB32 (16 МБ) + нормаль до
  `MaxNormalRenderTextureSize = 1024` (4 МБ) + глянец `GlossRtSize = 512` (1 МБ).
  `TakeRtBudget()` — это **ограничение скорости** (`RtCreationsPerRepaint = 1`,
  не больше одной RT за перерисовку), а не потолок резидентной памяти. То есть
  ~21 МБ несжатой VRAM × число слотов × число колонисток, и оно накапливается.
- Текстуры импортируются с `maxTextureSize: 2048` и оверрайдом только под
  `Standalone`. Оверрайд под `iPhone` есть лишь у 32 `.meta` из ~1600 — остальные
  поедут на дефолтных настройках (ASTC), что само по себе нормально, но 2048²
  ASTC 6×6 ≈ 1.4 МБ × ~1200 текстур ≈ 1.6 ГБ, если поедет всё.
- Лимиты App Store: несжатый бандл — до 4 ГБ; скачивание по сотовой сети выше
  ~200 МБ требует явного согласия пользователя.

**Значит нужен мобильный тир контента.** Не «сжать посильнее», а отдельная ветка
пайплайна:

1. **Децимация тел** — то, что для инструментов уже делается штатно
   (`TOOL_GENERATION_SPEC.md`: high-poly → UnityMeshSimplifier). Для тела и волос
   этого шага нет.
2. **Прополка блендшейпов** — оставить только те, что реально читаются:
   виземы `eCTRL*` для uLipSync (`NpcVoiceLipSync.cs:83`) и моргание
   (`NpcFaceAnimator.cs:83`). Остальные морфы Genesis в игре не используются, а
   весят они основную массу меша.
3. **Подмножество гардероба** — 1771 записи волос на телефоне не нужны. Нужен
   явный мобильный список, а не «то же самое, но пожатое».
4. **Снизить потолки RT** в `SkinTexturePainter` (2048/1024/512 → 512/256/128) и
   ввести настоящий потолок резидентных RT, а не только скорость создания.
5. **ASTC + `maxTextureSize` 1024** оверрайдами под `iPhone`.

Без пунктов 1–2 приложение будет падать по памяти на спавне колонии, и никакая
настройка качества это не спасёт.

---

## 5. Средняя по объёму работа

### Тач-ввод — маленький и приятный

Вся привязка к устройствам ввода в игровом коде:

| Файл | Строк | `Mouse.current` | `Keyboard.current` | `Touchscreen` |
|---|---|---|---|---|
| `Input/RtsCameraController.cs` | 849 | 4 | 3 | 0 |
| `Input/SimulationInputAdapter.cs` | 541 | 1 | 0 | 0 |
| `Input/OrbitCameraController.cs` | 102 | 1 | 0 | 0 |
| `Input/HexSelection.cs`, `NpcSelection.cs`, `ManualOrderFeedback.cs` | 229 | 0 | 0 | 0 |

Три точки. Нужен слой на `InputSystem.EnhancedTouch`: одним пальцем — панорама,
двумя — пинч-зум и поворот, тап — то, что сейчас левый клик, долгое нажатие —
контекстное меню §121 (`SimulationInputAdapter` уже строит его список записей).
`PickRadiusPixels = 70f` для пальца, кстати, стоит увеличить. Оценка: 1–2 дня.

### Прочее

- **UI Toolkit** — панели свёрстаны под десктоп (`DebugControlsPanel`,
  `BugReportPanel`, `CharacterPanel`, `GameHistoryPanel`, `SimSpeedBar`). Нужен
  масштаб под DPI телефона и safe-area (челка/Dynamic Island).
- **Стриппинг IL2CPP** — `link.xml` в проекте **нет вообще**, а рефлексия есть:
  `BalanceReflection` (перечисляет public static поля 20+ классов настроек),
  `SimConfigMirror`, `BalanceTuning`, и `LunarRuntimeConsoleProvider`, который
  ищет тип по строке `"LunarConsolePlugin.LunarConsole"`. Нужен `link.xml` либо
  `[Preserve]`, иначе поведение разъедется молча — ровно тот класс бага, от
  которого спасает `BalanceParityGate` в редакторе и который в плеере некому
  поймать.
- **Серверный режим** — `RemoteSocketBackend` на `ClientWebSocket` на iOS
  работает, но открытый `ws://` требует исключения ATS (`NSAllowsArbitraryLoads`)
  и портит ревью в App Store. Правильнее сразу `wss://`.
- **Багтрекер.** `PrototypeRuntimeBootstrap` создаёт `BugReportPanel`
  **вне** `#if UNITY_EDITOR || DEVELOPMENT_BUILD` — то есть окно поедет и в
  релизной сборке. На iOS `BugReportStore` свалится в `persistentDataPath` внутри
  песочницы, откуда игрок файл достать не может. Либо `UIFileSharingEnabled` +
  `LSSupportsOpeningDocumentsInPlace` (тогда `BUGS.json` виден в Files.app), либо
  отправка отчётов на сервер. Заодно решить, должен ли релиз вообще нести
  трекер и отладочную панель.
- **`com.coplaydev.unity-mcp`** — проверить, что asmdef пакета только Editor.
  Если его TCP-мост попадёт в плеер, это и вес, и почти наверняка отказ ревью.
- **Bundle id** — сейчас в `ProjectSettings.asset` стоит шаблонный
  `iPhone: com.Unity-Technologies.com.unity.template.urp-blank`. Нужен настоящий,
  совпадающий с App ID в аккаунте разработчика.
- **iPhone vs iPad.** `targetDevice: 2` — оба. Интерфейс колонии (панель
  персонажа, история, контекстные меню) на телефоне будет тесным; iPad как
  первичная цель разумнее.

### Сборочный конвейер

`Tools/build_release.py` (840 строк) — целиком про macOS: `codesign`, `defaults`
для Unity-префов, атомарная перенастройка `~/hex-girls/HexLive.app`, проверка
подписи. Единственная точка входа в Unity —
`HexLive.UnityDebug.Editor.HexLiveReleaseBuilder.BuildMacOS`, где жёстко
`BuildTarget.StandaloneOSX` и `BuildTargetGroup.Standalone`
(`HexLiveReleaseBuilder.cs:50,82`).

Для iOS нужен второй вход `BuildIOS`, отдающий Xcode-проект, и отдельная нога
`xcodebuild`/fastlane для подписи и TestFlight. Полезное здесь то, что вся
обвязка вокруг (версия, снапшот `BUGS.json`, список коммитов и грязных путей,
финализатор версии) переиспользуется как есть.

### Детерминизм — стоит подумать заранее

CLAUDE.md прямо говорит: «Float operation order IS behaviour», и золотые трейсы
на этом стоят. Симуляция на iOS пойдёт через IL2CPP/ARM64, где раскладка
плавающей арифметики (в частности FMA-слияние) не обязана совпадать с x86-64.
Практические следствия:

- сейв, сделанный на Mac, может разойтись с тем же сейвом на iPhone;
- `Tools/golden_trace.sh` сравнивает трейсы в пределах одной платформы и
  кросс-платформенное расхождение не поймает.

Если iOS-клиент будет **тонким** (сервер авторитетен — вся машинерия для этого
уже есть: `ISimulationBackend`, `RemoteSocketBackend`, дельты ~10 КБ/с, сервер
~40 МБ RSS), вопрос снимается полностью. Если симуляция локальная — надо решить,
допустимо ли расхождение, и по крайней мере не обещать переносимость сейвов.

Замечу сразу: серверный вариант **не спасает от блокера №3**. Он снимает
стоимость симуляции (~8 мс/тик), но узкое место на телефоне — не симуляция, а
рендер и память персонажей.

---

## 6. Что понадобится на билд-машине

- macOS + Xcode (текущая версия под iOS 15+ SDK);
- Unity **6000.4.5f1** с модулем **iOS Build Support** (версия обязана совпадать
  ровно — `ProjectVersion.txt`);
- заново скачанная `FMOD for Unity` **2.03.14** с iOS-библиотеками
  (`Assets/Plugins/FMOD/` в `.gitignore`);
- аккаунт разработчика — есть; нужны App ID, provisioning profile,
  запись приложения в App Store Connect для TestFlight;
- `git lfs pull` — без него меши и текстуры остаются указателями по 134 байта, и
  сборка молча соберётся с пустым контентом.

---

## 7. Предлагаемый порядок

**Фаза 1 — спайк «что-нибудь на устройстве», 3–5 дней.** Цель не красота, а
цифры. Урезать Addressables до горстки предметов, собрать iOS-банки FMOD,
положить каталог локально в StreamingAssets, заменить мышь на тап и драг,
поставить dev-билд на реальный iPhone/iPad через Xcode. Снять: время кадра,
пиковую память, время загрузки, размер IPA.

Всё дальнейшее планируется по этим четырём числам, а не по оценкам. Это ровно то,
чего требует правило «сначала ЗАМЕР, потом гипотеза» — «на телефоне будет
тормозить» без замера такая же ставка, как правка вида без кадра проверки.

**Фаза 2 — мобильный тир контента.** Децимация тел и волос, прополка
блендшейпов, мобильный список гардероба, потолки RT, ASTC-оверрайды. Это
основной объём и, вероятно, недели, а не дни.

**Фаза 3 — тач-UX и продакшен.** Переверстка UI Toolkit под safe-area, жесты,
`link.xml`, решение по багтрекеру и отладочной панели в релизе, ветка `BuildIOS`
и конвейер TestFlight.

**Развилку «локальная симуляция или тонкий клиент» лучше решить до фазы 2** — от
неё зависит и вопрос детерминизма, и часть бюджета памяти.
