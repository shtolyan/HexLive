# HexLive: инструкция Windows-агенту по сборке и публикации Atomic Content

Эта инструкция предназначена для текущей полной Windows-сборки независимых
AssetBundle. Она не собирает Windows Player и не изменяет production.

## Зафиксированный исходный код

- Ветка для получения изменений: `codex/content-release-fixes`.
- Объявленный build SHA:
  `49c03d5989d2ace629a932e3687f5f846703ce17`.
- Unity: `6000.4.5f1`.
- Платформа: `StandaloneWindows64`.
- Runtime profile: `unity6000-content1`.
- Ожидается ровно `2725` атомарных объектов.
- Ожидаемый SHA-256 отсортированного списка `type/id`:
  `731c9be10a7842247a795524fade2b5100b19f88ad64da4c8e197ef332fcbdb1`.

Сборка macOS и сборка Windows обязаны происходить из одного объявленного SHA.
Имя ветки само по себе недостаточно: после синхронизации нужно перейти в
detached HEAD именно на SHA выше.

## Что запрещено менять или собирать вручную

- Не переименовывать legacy ID с пробелами: `FCO * Male`,
  `FAO Harness Male`, `TonnyFlash` и названия цветов волос с пробелами валидны.
- Не создавать общий bundle иконок и не запускать отдельную сборку иконок.
  Настоящая иконка находится внутри bundle своего предмета.
- Не передавать `--icon` с прозрачным или любым другим плейсхолдером. Если
  настоящей иконки нет, bundle публикуется без `iconAsset`, а Player немедленно
  показывает штатный emoji fallback.
- Не собирать UI вручную. Bootstrap UI остаётся в Player, а каталог
  `RuntimeSource/UI` исключён из atomic content inventory.
- Не добавлять акулу: она отключена как незавершённый объект. Текущий mob
  inventory содержит только рабочий контент, обнаруженный `build-all`.
- Не собирать старые частичные очереди из 272/289 объектов и не дополнять их
  вручную. Нужен новый полный `build-all` в отдельную выходную папку.
- Не создавать вручную descriptors для RuntimeSource. Их перечисляет
  `Tools/content.py build-all`.
- Не запускать отдельно audio или `config/simdata`: `build-all` добавляет их
  автоматически.
- Не публиковать в production: запрещены порт `5123` и
  `/var/lib/hexlive/assets`.

## 1. Подготовка Windows-машины

Открыть PowerShell в корне HexLive. Unity Editor для этого checkout должен быть
закрыт: batchmode не может открыть проект одновременно с интерактивным Editor.
Если `Get-Process Unity` показывает живую сборку, не убивать её — дождаться
завершения или закрыть штатно.

Проверить свободное место и состояние проекта:

```powershell
Get-PSDrive -PSProvider FileSystem
Get-Process Unity -ErrorAction SilentlyContinue
git status --short
```

Если `git status --short` показывает исходники или ассеты с локальными
изменениями, остановиться. Не делать `reset`, не подмешивать эти изменения в
кандидаты и не использовать `stash` без согласования с владельцем изменений.

## 2. Получить точный SHA

```powershell
git fetch origin
git switch --detach 49c03d5989d2ace629a932e3687f5f846703ce17
git lfs pull
git lfs checkout
git rev-parse HEAD
git status --short
```

Обязательный вывод `git rev-parse HEAD`:

```text
49c03d5989d2ace629a932e3687f5f846703ce17
```

Если SHA отличается или LFS не может получить payload, сборку не начинать.
Не собирать AssetBundle из Git LFS pointer-файлов.

Проверить версию проекта:

```powershell
Get-Content ProjectSettings\ProjectVersion.txt
```

Ожидается `m_EditorVersion: 6000.4.5f1`. Эта команда не собирает Player и не
переключает проект на macOS; Unity запускается сразу с `StandaloneWindows64`.

## 3. Собрать полный независимый Windows inventory

Использовать новую выходную папку, не смешанную со старыми 272/289
кандидатами:

```powershell
$AtomicOutput = Join-Path (Get-Location) 'Build\AtomicContent\handoff-49c03d598\StandaloneWindows64'

py -3 Tools\content.py build-all `
  --platform StandaloneWindows64 `
  --runtime-profile unity6000-content1 `
  --output $AtomicOutput

if ($LASTEXITCODE -ne 0) {
    throw "Atomic content build failed with exit code $LASTEXITCODE"
}
```

Команда сама:

1. находит полный активный inventory;
2. собирает каждый Unity-объект отдельным самодостаточным bundle;
3. проверяет ноль внешних bundle dependencies;
4. включает `main`, обязательную metadata и настоящую owner icon, если она есть;
5. добавляет raw audio, `.vis`, FMOD banks и `config/simdata`;
6. считает SHA-256 каждого payload и записывает `candidate.json`.

Не запускать одновременно вторую Unity-сборку этого проекта.

## 4. Проверить результат до загрузки

Проверить summary:

```powershell
$SummaryPath = Join-Path $AtomicOutput 'build-all-summary.json'
$Summary = Get-Content $SummaryPath -Raw | ConvertFrom-Json
$Summary | Format-List platform, runtimeProfile, discovered, built, failed

if ($Summary.platform -ne 'StandaloneWindows64') { throw 'Wrong platform' }
if ($Summary.runtimeProfile -ne 'unity6000-content1') { throw 'Wrong runtime profile' }
if ([int]$Summary.discovered -ne 1596) { throw 'Wrong Unity object inventory' }
if ([int]$Summary.built -ne 1596) { throw 'Not all Unity objects were built' }
if ([int]$Summary.failed -ne 0) { throw 'Some bundles failed; do not publish' }
```

Посчитать полный inventory и его канонический digest. Этот блок использует
Python, чтобы сортировка была ordinal и одинаковой на Windows/macOS:

```powershell
@'
import hashlib
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
lines = []
for path in root.rglob("candidate.json"):
    value = json.loads(path.read_text(encoding="utf-8"))
    lines.append(f"{value['type']}/{value['id']}")
lines.sort()
payload = "".join(line + "\n" for line in lines).encode("utf-8")
print(f"objects={len(lines)}")
print(f"sha256={hashlib.sha256(payload).hexdigest()}")
'@ | py -3 - $AtomicOutput
```

Обязательный результат:

```text
objects=2725
sha256=731c9be10a7842247a795524fade2b5100b19f88ad64da4c8e197ef332fcbdb1
```

Дополнительно проверить распределение объектов и payload:

```powershell
@'
import collections
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
types = collections.Counter()
payloads = collections.Counter()
platforms = collections.Counter()
profiles = collections.Counter()
for path in root.rglob("candidate.json"):
    value = json.loads(path.read_text(encoding="utf-8"))
    types[value["type"]] += 1
    for variant in value["variants"]:
        payloads[variant["payloadType"]] += 1
        platforms[variant["platform"]] += 1
        profiles[variant["runtimeProfile"]] += 1
print("types:", " ".join(f"{k}={v}" for k, v in sorted(types.items())))
print("payloads:", " ".join(f"{k}={v}" for k, v in sorted(payloads.items())))
print("platforms:", dict(platforms))
print("profiles:", dict(profiles))
'@ | py -3 - $AtomicOutput
```

Ожидается:

```text
types: actor=5 audio=1128 building=7 config=754 hair=16 mob=2 object=51 prosthetic=8 vfx=69 wear=685
payloads: assetBundle=1596 file=1129
platforms: {'StandaloneWindows64': 2725}
profiles: {'unity6000-content1': 2725}
```

При любом расхождении не публиковать. Передать Марку:

- полный SHA из `git rev-parse HEAD`;
- весь `$SummaryPath`;
- фактические `objects`, digest и распределение;
- файл `$AtomicOutput\unity.log` при ошибке.

Не чинить несовпадение переименованием ID, плейсхолдером или ручным исключением.

## 5. Опубликовать только в изолированный staging

Публикацию начинает только один агент после того, как macOS-варианты уже
находятся в staging. Не запускать параллельный publish с Mac.

Сначала проверить SSH-доступ:

```powershell
ssh hexlive-server "systemctl is-active hexlive-staging.service"
```

Ожидается `active`. Если SSH alias/key отсутствует, ничего не публиковать и
сообщить владельцу. Публичный upload API создавать нельзя.

Загрузить Windows-варианты:

```powershell
py -3 Tools\content.py publish-all `
  --input $AtomicOutput `
  --host hexlive-server `
  --required-platform StandaloneWindows64 `
  --remote-root /var/lib/hexlive-staging/assets `
  --remote-user hexlive `
  --server-dll /opt/hexlive-staging/current/HexLive.Server `
  --retain-current-variants

if ($LASTEXITCODE -ne 0) {
    throw "Atomic content publish failed with exit code $LASTEXITCODE"
}
```

Ключ `--retain-current-variants` обязателен: он сохраняет уже проверенный
`StandaloneOSX` вариант того же объекта и добавляет Windows-вариант. Сервер
сам проверяет staged SHA/size, сериализует публикации одного ID и атомарно
переключает record. Повтор команды с теми же SHA безопасен.

Категорически не заменять staging-пути следующими production-путями:

```text
/var/lib/hexlive/assets
/opt/hexlive/current/HexLive.Server
port 5123
```

## 6. Проверить staging API после публикации

```powershell
$StagingApi = 'http://62.146.235.120:5124/api/assets/v1'
$Index = Invoke-RestMethod "$StagingApi/index/StandaloneWindows64/unity6000-content1"

"registryRevision=$($Index.registryRevision) objects=$($Index.objects.Count)"
if ($Index.objects.Count -ne 2725) {
    throw "Staging Windows index is incomplete: $($Index.objects.Count)/2725"
}
```

Проверить предмет с иконкой внутри его же bundle:

```powershell
$Machete = Invoke-RestMethod "$StagingApi/objects/object/tool.machete?platform=StandaloneWindows64&profile=unity6000-content1"
$Machete | ConvertTo-Json -Depth 8

if ($Machete.variant.platform -ne 'StandaloneWindows64') { throw 'Wrong machete platform' }
if ($Machete.variant.runtimeProfile -ne 'unity6000-content1') { throw 'Wrong machete profile' }
if ($Machete.variant.iconAsset -ne 'icon') { throw 'Machete owner icon is not embedded' }

curl.exe --fail --silent --show-error --head `
  "$StagingApi/blobs/$($Machete.variant.sha256)"
```

HEAD должен вернуть `200`, корректный `Content-Length` и
`Cache-Control: public,max-age=31536000,immutable`.

После проверки передать владельцу:

```text
Windows SHA: <полный git SHA>
Build objects: 2725
Inventory digest: 731c9be10a7842247a795524fade2b5100b19f88ad64da4c8e197ef332fcbdb1
Publish exit: 0
Staging registryRevision: <номер>
Staging Windows objects: 2725
```

Отдельно сообщить записи пяти последних визуальных исправлений:

```powershell
foreach ($Id in @(
    'shelter.tent',
    'station.drying_rack',
    'tool.bow',
    'resource.arrow',
    'furniture.wardrobe'
)) {
    Invoke-RestMethod "$StagingApi/objects/object/$Id`?platform=StandaloneWindows64&profile=unity6000-content1" |
        Select-Object type, id, revision, variant
}
```

У каждой записи после публикации обязан быть
`variant.platform=StandaloneWindows64`; `tool.bow` и `resource.arrow` не должны
иметь общих bundle dependencies. Иконка для них не собирается: UI немедленно
показывает `🏹` и `🎯`. Windows bundle `furniture.wardrobe` обязан
содержать `HangerTemplate`; его нельзя заменять процедурными
`Cube`/`Sphere`/`Cylinder`.

## 7. Последующие атомарные обновления одного объекта

Это не используется для первого полного bootstrap. После его приёмки одну
вещь можно пересобрать отдельно, например:

```powershell
$OneOutput = Join-Path (Get-Location) 'Build\AtomicContent\single\FCO Pants Male\StandaloneWindows64'

py -3 Tools\content.py build `
  --type wear `
  --id 'FCO Pants Male' `
  --platform StandaloneWindows64 `
  --runtime-profile unity6000-content1 `
  --output $OneOutput
```

Не передавать `--icon`: настоящий icon автоматически попадает в этот же owner
bundle. Публикация одного Windows candidate выполняется только после сборки
соответствующего macOS-варианта или при наличии совместимого macOS-варианта в
staging, с теми же правилами exact SHA и `--retain-current-variants`.

Канонический расширенный runbook находится в
`Docs/AtomicContentCrossPlatform.md`, архитектурный контракт — в `Spec/152.md`.
