# Доступ агентов к серверам HexLive

У проекта два независимых сервера. Название сервера в задаче всегда важнее
алиаса по умолчанию: нельзя переносить состояние, контент или конфигурацию с
одного сервера на другой без прямого указания игрока.

| Сервер | Адрес | SSH-алиас | Роль |
| --- | --- | --- | --- |
| Сингапур | `62.146.235.120` | `hexlive-singapore` (`hexlive-server` — старый алиас) | Отдельный ранее развёрнутый сервер; не изменять без явного указания |
| Нью-Йорк | `163.245.204.96` | `hexlive-nyc` | Текущий production и цель канонического runbook |

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
