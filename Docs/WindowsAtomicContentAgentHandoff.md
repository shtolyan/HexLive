# HexLive: Windows Atomic Content — сборка и публикация в production

Эта инструкция собирает полный вариант независимых AssetBundle для
`StandaloneWindows64` и добавляет его в уже работающий production-реестр.
Windows Player она не собирает.

## Зафиксированный контракт

- Единственная ветка-источник: `origin/master`.
- Content baseline: `ad1b14e1a0e3810778c21835503dae91d5ded43a`.
- Unity: `6000.4.5f1`.
- Платформа: `StandaloneWindows64`.
- Runtime profile: `unity6000-content1`.
- Production API: `https://163-245-204-96.sslip.io/api/assets/v1`.
- Production service: `hexlive.service`.
- Production asset root: `/var/lib/hexlive/assets`.
- Production publisher: `/opt/hexlive/current/HexLive.Server`.
- Полный активный inventory: `2725` объектов.
- SHA-256 отсортированного списка `type/id`:
  `731c9be10a7842247a795524fade2b5100b19f88ad64da4c8e197ef332fcbdb1`.

Baseline — commit из `master`, из которого собран текущий content contract.
Документационные commit после него допустимы. Изменения в `Assets`, `Packages`,
`ProjectSettings`, `SimData` или `Tools/content.py` после baseline требуют сначала
согласованно обновить macOS-варианты; приведённый ниже gate такую сборку остановит.
Текущий baseline включает reference-only repair native prefab `tool.bottle` для
Unity 6000.4.5f1: геометрия и metadata существующего macOS-варианта не менялись.

Эта инструкция работает только с production `hexlive.service` за TLS-хостом
`163-245-204-96.sslip.io`. Прямой порт `5123` закрыт файрволом.

Windows SSH config должен содержать отдельный ограниченный алиас:

```sshconfig
Host hexlive-content-nyc
    HostName 163.245.204.96
    User hexlive-content
    IdentityFile ~/.ssh/hexlive_windows_content_ed25519
    IdentitiesOnly yes
```

Приватный ключ не хранится в репозитории. Сервер принимает его публичную часть
и разрешает только штатные команды staging/publish из `Tools/content.py`.

## Что нельзя делать

- Не переходить в старую feature-ветку и не брать из неё отдельные commit.
- Не делать `reset`, не удалять и не подмешивать чужие локальные изменения.
- Не собирать Addressables catalog, content release или Windows Player.
- Не создавать общий bundle иконок и не запускать отдельную сборку иконок.
  Настоящая иконка входит в bundle своего объекта как `icon`; при её отсутствии
  Player сразу показывает штатный emoji fallback.
- Не передавать `--icon` с плейсхолдером.
- Не переименовывать legacy ID с пробелами. Текущий контракт допускает пробелы в
  `FCO * Male`, `FAO Harness Male`, `TonnyFlash` и именах вариантов волос.
- Не собирать bootstrap UI: он остаётся в Player; отдельного UI bundle нет.
- Не возвращать акулу: незавершённый объект исключён из inventory.
- Не собирать вручную старые частичные очереди. Нужен новый полный `build-all`.
- Не запускать одновременно две Unity-сборки или два publisher.
- Не публиковать через HTTP: публичного upload API нет, используется только SSH.

## 1. Подготовить чистый `master`

Открыть PowerShell в корне HexLive. Интерактивный Unity Editor для этого checkout
должен быть закрыт. Не завершать чужой Unity-процесс принудительно.

```powershell
Get-PSDrive -PSProvider FileSystem
Get-Process Unity -ErrorAction SilentlyContinue
git status --short
```

Для полной очереди желательно иметь не менее 25 ГБ свободного места. Если
`git status --short` показывает локальные изменения исходников или ассетов,
остановиться и сообщить их список владельцу checkout.

Получить только `master`:

```powershell
$ContentBaseline = 'ad1b14e1a0e3810778c21835503dae91d5ded43a'

git fetch origin
git switch master
git pull --ff-only origin master
git lfs pull
git lfs checkout

$BuildSha = (git rev-parse HEAD).Trim()
$OriginMaster = (git rev-parse origin/master).Trim()
"BuildSha=$BuildSha"
"OriginMaster=$OriginMaster"

if ($BuildSha -ne $OriginMaster) {
    throw 'Local master is not exactly origin/master'
}

git merge-base --is-ancestor $ContentBaseline $BuildSha
if ($LASTEXITCODE -ne 0) {
    throw 'Content baseline is not in current master history'
}

git diff --quiet $ContentBaseline $BuildSha -- `
    Assets Packages ProjectSettings SimData Tools/content.py
if ($LASTEXITCODE -ne 0) {
    throw 'Content-relevant files changed after the coordinated baseline; do not build'
}

if (git status --porcelain) {
    throw 'Checkout is dirty after synchronization; do not build'
}
```

Эта проверка оставляет агента на актуальном `master`; detached HEAD и feature-
ветки не используются. Проверить Unity:

```powershell
Get-Content ProjectSettings\ProjectVersion.txt
```

Ожидается `m_EditorVersion: 6000.4.5f1`. Если LFS не скачан полностью,
`Tools/content.py` дополнительно обнаружит pointer-файлы и остановит сборку.

## 2. Проверить production до сборки

```powershell
$ProdApi = 'https://163-245-204-96.sslip.io/api/assets/v1'
$MacBefore = Invoke-RestMethod `
  "$ProdApi/index/StandaloneOSX/unity6000-content1"
$WinBefore = Invoke-RestMethod `
  "$ProdApi/index/StandaloneWindows64/unity6000-content1"

"registryRevision=$($MacBefore.registryRevision) macOS=$($MacBefore.objects.Count) Windows=$($WinBefore.objects.Count)"

if ($MacBefore.objects.Count -ne 2725) {
    throw 'Production macOS inventory is incomplete; do not publish Windows variants'
}
if ($WinBefore.objects.Count -ne 0) {
    throw 'Production already contains Windows variants; stop and reconcile before bootstrap'
}
if ($MacBefore.registryRevision -ne $WinBefore.registryRevision) {
    throw 'Production index snapshots disagree; repeat preflight'
}

$RegistryBefore = [int64]$MacBefore.registryRevision
```

На момент актуализации инструкции ожидается `registryRevision=4518`,
`macOS=2725`, `Windows=0`. Если данные изменились, не подгонять проверки вручную.

## 3. Собрать полный Windows inventory

Использовать новую папку, не смешанную со старыми кандидатами:

```powershell
$ShortSha = $BuildSha.Substring(0, 12)
$AtomicOutput = Join-Path (Get-Location) `
  "Build\AtomicContent\windows-$ShortSha\StandaloneWindows64"

if (Test-Path $AtomicOutput) {
    throw "Output already exists: $AtomicOutput"
}

py -3 Tools\content.py build-all `
  --platform StandaloneWindows64 `
  --runtime-profile unity6000-content1 `
  --output $AtomicOutput

if ($LASTEXITCODE -ne 0) {
    throw "Atomic content build failed; inspect $AtomicOutput\unity.log"
}
```

`build-all` сам:

1. перечисляет весь активный inventory;
2. собирает каждый Unity-объект в отдельный самодостаточный bundle;
3. проверяет ноль внешних bundle dependencies;
4. кладёт `main`, metadata и owner icon в один bundle объекта;
5. добавляет raw audio, `.vis`, FMOD banks и `config/simdata`;
6. вычисляет SHA-256 и создаёт `candidate.json` для каждого объекта.

## 4. Провалидировать кандидаты

Проверить Unity summary:

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

Посчитать полный inventory ordinal-сортировкой:

```powershell
@'
import collections
import hashlib
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
lines = []
types = collections.Counter()
payloads = collections.Counter()
platforms = collections.Counter()
profiles = collections.Counter()

for path in root.rglob("candidate.json"):
    value = json.loads(path.read_text(encoding="utf-8"))
    lines.append(f"{value['type']}/{value['id']}")
    types[value["type"]] += 1
    for variant in value["variants"]:
        payloads[variant["payloadType"]] += 1
        platforms[variant["platform"]] += 1
        profiles[variant["runtimeProfile"]] += 1

lines.sort()
digest = hashlib.sha256(
    "".join(line + "\n" for line in lines).encode("utf-8")
).hexdigest()
print(f"objects={len(lines)}")
print(f"sha256={digest}")
print("types:", " ".join(f"{k}={v}" for k, v in sorted(types.items())))
print("payloads:", " ".join(f"{k}={v}" for k, v in sorted(payloads.items())))
print("platforms:", dict(platforms))
print("profiles:", dict(profiles))
'@ | py -3 - $AtomicOutput

if ($LASTEXITCODE -ne 0) { throw 'Candidate audit failed' }
```

Обязательный результат:

```text
objects=2725
sha256=731c9be10a7842247a795524fade2b5100b19f88ad64da4c8e197ef332fcbdb1
types: actor=5 audio=1128 building=7 config=754 hair=16 mob=2 object=51 prosthetic=8 vfx=69 wear=685
payloads: assetBundle=1596 file=1129
platforms: {'StandaloneWindows64': 2725}
profiles: {'unity6000-content1': 2725}
```

При любом расхождении ничего не публиковать. Сохранить `$BuildSha`, summary,
фактический digest и `unity.log` при ошибке.

## 5. Опубликовать в production по SSH

Публикацию запускает только один агент. `--retain-current-variants` обязателен:
он сохраняет проверенный `StandaloneOSX` вариант и добавляет Windows-вариант к
той же атомарной записи.

```powershell
py -3 Tools\content.py publish-all `
  --input $AtomicOutput `
  --host hexlive-content-nyc `
  --required-platform StandaloneWindows64 `
  --remote-root /var/lib/hexlive/assets `
  --remote-user hexlive `
  --server-dll /opt/hexlive/current/HexLive.Server `
  --retain-current-variants

if ($LASTEXITCODE -ne 0) {
    throw 'Production publish failed; do not start another publisher'
}
```

Команда сначала локально перепроверяет каждый `candidate.json`, payload size и
SHA-256, затем одним архивом кладёт bytes во временный SSH-каталог. Сервер публикует
объекты по одному под межпроцессной блокировкой и атомарно меняет каждую record.
Повтор той же полностью проверенной команды безопасен: уже совпавшие записи
будут `no-op`. При ошибке не создавать ручные records и не переносить blobs
вручную.

## 6. Проверить production после публикации

```powershell
$MacAfter = Invoke-RestMethod `
  "$ProdApi/index/StandaloneOSX/unity6000-content1"
$WinAfter = Invoke-RestMethod `
  "$ProdApi/index/StandaloneWindows64/unity6000-content1"

"registryRevision=$($WinAfter.registryRevision) macOS=$($MacAfter.objects.Count) Windows=$($WinAfter.objects.Count)"

if ($MacAfter.objects.Count -ne 2725) { throw 'macOS variants were lost' }
if ($WinAfter.objects.Count -ne 2725) { throw 'Windows inventory is incomplete' }
if ($MacAfter.registryRevision -ne $WinAfter.registryRevision) {
    throw 'Platform indexes disagree after publish'
}
if ([int64]$WinAfter.registryRevision -ne ($RegistryBefore + 2725)) {
    throw 'Unexpected registry delta; inspect concurrent or partial publication'
}
```

Проверить owner icon и immutable blob:

```powershell
$Machete = Invoke-RestMethod `
  "$ProdApi/objects/object/tool.machete?platform=StandaloneWindows64&profile=unity6000-content1"

if ($Machete.variant.platform -ne 'StandaloneWindows64') { throw 'Wrong platform' }
if ($Machete.variant.entryAsset -ne 'main') { throw 'Missing main asset' }
if ($Machete.variant.iconAsset -ne 'icon') { throw 'Owner icon is not embedded' }

curl.exe --fail --silent --show-error --head `
  "$ProdApi/blobs/$($Machete.variant.sha256)"
curl.exe --fail --silent --show-error --range 0-1023 --output NUL `
  --write-out "HTTP %{http_code}, bytes %{size_download}`n" `
  "$ProdApi/blobs/$($Machete.variant.sha256)"
```

HEAD должен вернуть `200`, правильный `Content-Length`, `Accept-Ranges: bytes`,
ETag с SHA и `Cache-Control: public,max-age=31536000,immutable`. Range должен
вернуть `206`.

Проверить несколько критичных world objects:

```powershell
foreach ($Id in @(
    'shelter.tent',
    'station.drying_rack',
    'tool.bow',
    'resource.arrow',
    'furniture.wardrobe'
)) {
    $Record = Invoke-RestMethod `
      "$ProdApi/objects/object/$Id`?platform=StandaloneWindows64&profile=unity6000-content1"
    if ($Record.variant.platform -ne 'StandaloneWindows64') {
        throw "Missing Windows variant: $Id"
    }
    $Record | Select-Object type, id, revision, variant
}
```

`tool.bow` и `resource.arrow` не имеют внешних bundle dependencies. Если у них
нет authored icon, UI использует emoji `🏹` и `🎯`; отдельного icon bundle нет.
`furniture.wardrobe` содержит `HangerTemplate` и не должен заменяться
процедурными `Cube`/`Sphere`/`Cylinder`.

Проверить, что world server продолжает работать:

```powershell
Invoke-WebRequest 'https://163-245-204-96.sslip.io/' -UseBasicParsing
```

Итоговый отчёт должен содержать:

```text
Branch: master
Build SHA: <полный SHA origin/master>
Unity bundles: 1596/1596, failed=0
Objects: 2725
Inventory digest: 731c9be10a7842247a795524fade2b5100b19f88ad64da4c8e197ef332fcbdb1
Publish exit: 0
Production macOS objects: 2725
Production Windows objects: 2725
Registry revision before/after: <до>/<после>
hexlive.service: active
```

## 7. Последующее обновление одного объекта

Для изменения одной вещи не запускают `build-all`. Windows-кандидат собирается
отдельно:

```powershell
$OneOutput = Join-Path (Get-Location) `
  'Build\AtomicContent\single\FCO Pants Male\StandaloneWindows64'

py -3 Tools\content.py build `
  --type wear `
  --id 'FCO Pants Male' `
  --platform StandaloneWindows64 `
  --runtime-profile unity6000-content1 `
  --output $OneOutput
```

Не передавать `--icon`: authored icon автоматически входит в owner bundle.
Семантическое обновление должно иметь согласованные macOS и Windows candidates;
их публикуют вместе одной командой `content.py publish` с двумя `--candidate` и
двумя `--required-platform`. `--retain-current-variants` используется только
для добавления отсутствующего платформенного варианта с неизменной metadata, а
не для выпуска разных версий объекта под одним revision.

Архитектурный контракт: `Spec/152.md`. Команды этой инструкции являются
актуальным production flow.
