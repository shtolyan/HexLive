# Доступ агентов к серверам HexLive

У проекта два независимых сервера. Название сервера в задаче всегда важнее
алиаса по умолчанию: нельзя переносить состояние, контент или конфигурацию с
одного сервера на другой без прямого указания игрока.

> **Жёсткая граница:** настройка ключа, SSH, DNS, TLS или service env не является
> разрешением на обновление игрового сервера. Не собирать и не загружать новый
> бинарник, не переключать `/opt/hexlive/current`, не менять content/simdata и не
> требовать новый клиент без отдельной явной команды игрока на deploy именно
> этого VPS. Если без новой версии задачу выполнить нельзя, остановиться и
> запросить разрешение, заранее указав влияние на совместимость клиента.

| Сервер | Адрес | SSH-алиас | Роль |
| --- | --- | --- | --- |
| Сингапур | `62.146.235.120` | `hexlive-singapore` (`hexlive-server` — старый алиас) | Отдельный ранее развёрнутый сервер; не изменять без явного указания |
| Нью-Йорк | `163.245.204.96` | `hexlive-nyc` | Текущий production и цель канонического runbook |

## MSI/MCI: полный доступ разработчика к обоим серверам

Ключ ноутбука `%USERPROFILE%\.ssh\hexlive_windows_content_ed25519` разрешён
для пользователя `root` на Сингапуре и в Нью-Йорке. Старое имя файла содержит
`content` только по исторической причине: этот ключ даёт полный доступ к
службам, конфигурации, логам и деплою на обоих серверах.

- fingerprint: `SHA256:JWmeE/liz1ez6vp/x0TeV37lTvJQQSHLi7jkto8Z/wc`;
- комментарий публичного ключа: `natepo4ty@gmail.com`;
- приватный ключ остаётся только в профиле пользователя ноутбука и не должен
  попадать в репозиторий, чат или логи.

На MSI/MCI добавить в `%USERPROFILE%\.ssh\config`:

```sshconfig
Host hexlive-singapore
    HostName 62.146.235.120
    User root
    IdentityFile ~/.ssh/hexlive_windows_content_ed25519
    IdentitiesOnly yes

Host hexlive-nyc
    HostName 163.245.204.96
    User root
    IdentityFile ~/.ssh/hexlive_windows_content_ed25519
    IdentitiesOnly yes
```

Проверка из PowerShell:

```powershell
ssh -o BatchMode=yes hexlive-singapore 'hostname; id; systemctl is-active hexlive.service caddy.service'
ssh -o BatchMode=yes hexlive-nyc 'hostname; id; systemctl is-active hexlive.service caddy.service'
```

Обе команды должны показать `uid=0(root)` и два раза `active`. Для просмотра
логов использовать, например:

```powershell
ssh hexlive-nyc 'journalctl -u hexlive.service -n 200 --no-pager'
```

Ограниченный аккаунт `hexlive-content` можно продолжать использовать для
обычной публикации бандлов, но он не заменяет эти root-алиасы.

## Голосовая админка: DeepSeek на обоих серверах

Голосовая админка состоит из двух независимых частей: Deepgram переводит голос
в текст, а отдельная служба `hexlive-admin-agent.service` передаёт текст
`deepseek-v4-pro` и разрешает модели только типизированные MCP-команды §161.
Служба не имеет shell-инструмента и доступа к сейвам или репозиторию.

Секреты на каждом VPS хранятся вне release:

- `/etc/hexlive/admin-agent-token.env` — уникальный токен локальной MCP-шины;
- `/etc/hexlive/deepseek-admin.env` — DeepSeek API key;
- оба файла `root:root`, mode `0600`.

`hexlive.service` должен читать только `admin-agent-token.env` через drop-in
`20-admin-agent.conf`. Служба агента читает оба файла. После обновления игрового
сервера эти файлы и drop-in нельзя удалять. Проверка без вывода секретов:

```bash
ssh hexlive-nyc \
  'systemctl is-active hexlive.service hexlive-admin-agent.service; \
   stat -c "%a %U:%G %n" /etc/hexlive/admin-agent-token.env \
   /etc/hexlive/deepseek-admin.env; \
   systemctl show hexlive.service -p EnvironmentFiles --no-pager'
```

Ожидаются два `active`, права `600 root:root` и только
`admin-agent-token.env` в окружении игрового сервера. Idle-служба не вызывает
DeepSeek. При замене ключа достаточно атомарно заменить
`deepseek-admin.env` и перезапустить только `hexlive-admin-agent.service`.

## Нью-Йорк: общий SSH-ключ

Приватный deploy-ключ намеренно хранится в этом **закрытом** репозитории по
прямому указанию владельца:

- `.agents/servers/new-york/hexlive_163_245_204_96_ed25519` — приватный ключ;
- `.agents/servers/new-york/hexlive_163_245_204_96_ed25519.pub` — публичный ключ;
- fingerprint: `SHA256:wBq0/DPdWuufBePEHvqcr43Nw2vLdPC0cFl2lqos3m4`.

Ключ даёт доступ `root` к Нью-Йорку. Не печатать его содержимое в логи и чат,
не копировать в другие файлы репозитория. Если репозиторий станет публичным или
ключ попадёт наружу, немедленно заменить ключ на сервере и в репозитории.

### macOS / Linux

Из корня репозитория:

```bash
install -d -m 700 "$HOME/.ssh"
install -m 600 .agents/servers/new-york/hexlive_163_245_204_96_ed25519 \
  "$HOME/.ssh/hexlive_163_245_204_96_ed25519"
install -m 644 .agents/servers/new-york/hexlive_163_245_204_96_ed25519.pub \
  "$HOME/.ssh/hexlive_163_245_204_96_ed25519.pub"
```

Добавить в `~/.ssh/config`:

```sshconfig
Host hexlive-nyc
    HostName 163.245.204.96
    User root
    IdentityFile ~/.ssh/hexlive_163_245_204_96_ed25519
    IdentitiesOnly yes
```

### Windows PowerShell

Из корня репозитория:

```powershell
$sshDir = Join-Path $env:USERPROFILE ".ssh"
$privateKey = Join-Path $sshDir "hexlive_163_245_204_96_ed25519"
$publicKey = "$privateKey.pub"
New-Item -ItemType Directory -Force $sshDir | Out-Null
Copy-Item ".agents\servers\new-york\hexlive_163_245_204_96_ed25519" $privateKey -Force
Copy-Item ".agents\servers\new-york\hexlive_163_245_204_96_ed25519.pub" $publicKey -Force
icacls $privateKey /inheritance:r
icacls $privateKey /grant:r "$($env:USERNAME):(R)"
```

Добавить в `%USERPROFILE%\.ssh\config` тот же блок `Host hexlive-nyc`, что
показан выше. Если OpenSSH всё ещё считает права ключа слишком широкими,
проверить `icacls $privateKey`: доступ на чтение должен остаться только у
текущего пользователя.

### Проверка

```bash
ssh -o BatchMode=yes hexlive-nyc 'hostname; systemctl is-active hexlive.service'
```

Ожидается имя удалённой машины и `active`. При первом соединении сверить
показанный host fingerprint с уже доверенным источником, а не отключать
проверку host key. Полное обновление Нью-Йорка выполнять по разделу
«Обновление production-сервера» в `CLAUDE.md`.

## Голос и распознавание речи на Нью-Йорке

Сингапур и Нью-Йорк намеренно используют один Deepgram API key. На Нью-Йорке
он хранится вне репозитория в `/etc/hexlive/deepgram.env` с правами `0600` и
владельцем `hexlive:hexlive`. Systemd подключает файл через
`/etc/systemd/system/hexlive.service.d/10-deepgram.conf`.

При обновлении сервера эти файлы нельзя удалять или заменять пустыми. Без них
сервер продолжает игру, но сообщает в `/watch` `SttAvailable=false`, поэтому
Player показывает полупрозрачную некликабельную кнопку микрофона. После
исправления и restart уже открытый Player обязан переподключиться: capability
передаётся только в новом handshake.

Безопасная серверная проверка, не выводящая ключ:

```bash
ssh hexlive-nyc \
  'stat -c "%a %U:%G %s %n" /etc/hexlive/deepgram.env; \
   systemctl show hexlive.service -p EnvironmentFiles -p DropInPaths --no-pager'
```

Этого недостаточно для приёмки: финальная проверка должна авторизоваться
игровым player token, получить `control=true`, `agent=true`, `stt=true` в WSS
handshake и успешно запросить краткоживущий STT-token. Значения master key и
временного token никогда не писать в вывод. Кнопка микрофона не зависит от
Agent Studio: Studio даёт attachment агента, а сервер отдельно выдаёт Player
доступ к Deepgram.

## Сингапур

Сингапур использует отдельный локальный ключ
`~/.ssh/hexlive_62_146_235_120_ed25519`; его приватная часть в репозиторий этой
задачей не добавлялась. Рекомендуемый алиас:

```sshconfig
Host hexlive-singapore
    HostName 62.146.235.120
    User root
    IdentityFile ~/.ssh/hexlive_62_146_235_120_ed25519
    IdentitiesOnly yes
```

Старый алиас `hexlive-server` указывает на тот же адрес и оставлен только для
совместимости со старыми командами. Новые инструкции обязаны использовать
явные алиасы `hexlive-singapore` или `hexlive-nyc`.
