# Обновления HexLive (§166)

Сборка и публикация — разные операции. Agent Studio не изменяется.

Drive владельца `natepo4ty@gmail.com`:
- [HexLive Releases](https://drive.google.com/drive/folders/1AbKZ-I1Yis0uNYDptBDT7d9ySpRKbOho)
- [Client](https://drive.google.com/drive/folders/1jRbkDUlaHsyInES8in-Wxf8zKujUFwaa)
- [AgentStudio](https://drive.google.com/drive/folders/1uXwld4XYfyBhKAACjjg9XW26FHOJiQJg)

## Однократная настройка

1. Установить requirements.txt в отдельный venv. В Google Cloud включить Drive API,
   создать Desktop OAuth client для издателя и service account для читателя.
   OAuth client JSON и refresh credentials хранить вне репозитория. OAuth scope
   издателя — Drive (нужен доступ к папке, созданной подключением чата, а не этим
   OAuth-приложением). Скрипт использует только явно указанную папку релизов.
2. Выполнить `release.py authorize --client-secret <local-json> --output <local-credentials>`.
   В браузере войти как владелец диска. Не передавать JSON/токены через чат.
3. Выдать email сервисного аккаунта роль reader только на папку Client, без
   уведомления по email. Его key JSON установить на сервер в
   `/etc/hexlive/release-reader.json`, mode 0600, owner hexlive.
4. На сборщике ключ выпуска находится в `~/.config/hexlive/releases/signing-key.pem`.
   Его public half — release-public.pem и trust.json. Другому сборщику приватный
   ключ передаёт владелец безопасным способом. Не генерировать новый ключ поверх
   старого: уже выпущенный клиент доверяет текущему public key.
5. На сервер установить Tools/updates и venv в `/opt/hexlive-release-sync`, создать
   `/var/lib/hexlive/client-releases` для пользователя hexlive и установить unit
   `hexlive-release-sync.service`. Сервер игры содержит read-only API v2; изменение
   серверного бинарника разворачивать штатно с сохранением мира и проверками.
   Файлы reader credentials не должны входить в публичный каталог.
6. Для публичного macOS канала настроить Developer ID и нотарификацию согласно
   `Tools/MACOS_SIGNING.md`. Текущая локальная Apple Development identity этому
   требованию не соответствует. Издатель отклоняет ненотарифицированную сборку.

## Сборка и выпуск

Закрыть Unity Editor; использовать `Tools/build_release.py --release` на macOS
или `Tools/build_release_windows.py --release` на Windows. Они включают
self-contained updater до финальной подписи. Universal macOS Player содержит два helper, arm64 и x64; клиент выбирает
архитектуру своего процесса. Один universal архив публикуется в оба канала
архитектуры отдельными вызовами publish.

После штатной подписи и, для Mac, нотарификации/stapling:

```sh
python Tools/updates/release.py publish <version-directory> \
  --platform macos --architecture arm64 --minimum-version 0.1.0 \
  --credentials <publisher-credentials.json> \
  --signing-key <signing-key.pem> --notes-en <notes-en.txt> --notes-ru <notes-ru.txt>
```

Для Windows выбрать windows/x64. Минимальную версию задавать осознанно.
Проверить `sync-status.json`, `journalctl -u hexlive-release-sync`, API `/latest`,
Range download архива и установку на тестовой машине до объявления выпуска.
Архив сначала проверяется Drive checksum, метаданные публикуются последними;
сервер не меняет свой latest до полного скачивания и проверки SHA-256.

## Восстановление

Helper хранит `.hexlive-updates/journal.json` рядом с приложением и старую
сборку; при ошибке переключения возвращает её. `client-updates/install-*/updater.log`
в persistentDataPath объясняет ошибку. Не удалять старые версии при диагностике.
При обрыве процесса helper журнал восстанавливается при следующей попытке.
При обязательном неработающем выпуске публиковать исправленную сборку с большим
номером, а не подменять архив старого номера. При недоступности Drive кэш работает.
Сохранения и серверный мир updater не мигрирует.

Автотесты: `dotnet test Tests/HexLive.Updater.Tests`; Python: unittest discover
в Tools/updates/tests. Проверка двух реальных сборок на обеих ОС и Unity UI —
обязательная приёмка, одни headless-тесты её не заменяют.

## Проверка готовности Mac перед выпуском

Команда ничего не собирает, не подписывает, не загружает и не разворачивает:

```sh
python Tools/updates/preflight.py --release ~/hex-girls/Releases/v<VERSION> \
  --credentials ~/.config/hexlive/releases/publisher-credentials.json \
  --reader-credentials ~/.config/hexlive/releases/reader-credentials.json
```

Она проверяет упаковку обеих архитектур, совпадение версии в Info.plist и
манифесте, Developer ID/нотарификацию/Gatekeeper, локальные файлы Google
credentials, ключ метаданных и доступность HTTPS feed. Код выхода 1 и
`ready: false` означают незавершённую подготовку. `--offline` пропускает сетевые
проверки. Наличие credential-файла не подтверждает права в Google; после настройки
их проверяют реальными publish/sync. Reader может находиться только на сервере;
тогда его проверку выполняют там, не копируя секреты в репозиторий.

Перед первой публикацией `/latest` возвращает 404: нужно отличать ещё пустой feed
от неразвёрнутого API по состоянию сервиса на сервере. Штатный HTTPS host —
`163-245-204-96.sslip.io`, с дефисами. Выпуск отклоняется до загрузки в Drive,
если в universal `.app` отсутствует helper arm64 или x64 либо их архитектура
не совпадает с именем. Обновлятор проверяет точный номер версии в подтверждении
запуска и атомарно переключает macOS latest-ссылку; прежний выпуск остаётся
доступен для отката.

### Проверено 2026-09-21

- Mac-тесты используют временные приложения и реальные процессы: установка из
  обычной `.app` и relative symlink, успешный перезапуск с аргументами, тайм-аут,
  неверное подтверждение, откат сломанной ссылки и восстановление журнала.
- Архив через `ditto` сохраняет исполняемые права и framework-ссылки; выходящая
  за приложение ссылка отклоняется до распаковки.
- Локальные HTTP-тесты проверяют ETag и докачку для macOS arm64/x64 и Windows x64.
- Установленная `v0.1.110` не содержит helper. Google credentials и Developer ID
  ещё не подготовлены (подтверждено владельцем). Публичный Mac-канал не запущен;
  требуется настройка подписи/нотарификации по `Tools/MACOS_SIGNING.md`, Google
  OAuth и reader, публикация первого клиента с updater и приёмка двух версий.
  Первый такой клиент устанавливается вручную. Серверные изменения требуют
  отдельного согласованного развёртывания; тесты сами production не обновляют.

## История первичной настройки (2026-09-13)

Папки Drive созданы и readback подтвердил владельца. Создан отдельный локальный
RSA-3072 ключ; в репозитории только публичная часть. Google Cloud открыт под
правильным аккаунтом, но автоматическая проверка запретила создание проекта без
отдельного явного согласия. Поэтому service account, OAuth client, серверная
синхронизация и публичный первый релиз пока не активированы.

Проверены компиляция UnityPresentation с новыми файлами, сборка сервера,
self-contained helper osx-arm64, тесты updater, HTTP Range/ETag, подписи Python,
атомарность метаданных и SpecStructureGate. Реальный цикл двух Player-сборок на
macOS/Windows, проверка UI в Unity и нотарификация ещё не выполнены.
