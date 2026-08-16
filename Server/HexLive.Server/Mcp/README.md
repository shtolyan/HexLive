# HexLive MCP control (§144)

Внешний агент отдаёт приказы колонисткам тем же швом, что и клики игрока.
Полное «почему» — в `Spec/144.md`; здесь только как включить и позвать.

## Включение

```bash
dotnet run --project Server/HexLive.Server -- --port 5123 --mcp
```

Выключено по умолчанию. При первом запуске токен печатается в консоль один раз
и сохраняется рядом с сейвом в `hexlive-mcp.txt` (0600). Дальше читается оттуда.

`--mcp-lease N` — сколько секунд владение живёт без команд (по умолчанию 120).

## Подключение агента

Транспорт — HTTP, поэтому подключается как удалённый MCP-сервер:

```bash
claude mcp add --transport http hexlive http://localhost:5123/mcp \
  --header "Authorization: Bearer $(cat ~/hex-girls/ServerLocal/hexlive-mcp.txt)"
```

Проверить руками, без агента:

```bash
TOKEN=$(cat ~/hex-girls/ServerLocal/hexlive-mcp.txt)
curl -s -X POST http://localhost:5123/mcp \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

## Порядок работы

`list_colonists` → `describe_colonist` → `acquire_control` → приказы →
`release_control`.

`objectId` для `interact` и `mobId` для `attack_mob` берутся из сводки
восприятия в `describe_colonist`, а не выдумываются.

## Что здесь принципиально

- **Агент не может больше игрока.** Всё уходит в `WorldHost.SubmitManualCommand`
  и получает тот же вердикт accepted/rejected. Таблица разрешений §121.5 одна.
- **Одна колонистка — один владелец.** Лиз занимается вместе с ручным режимом и
  протухает по реальному времени. Владелец — сессия `Mcp-Session-Id`.
- **Отказ инструмента — не ошибка протокола**: приходит текстом с причиной.
- ⚠️ **Токен по простому HTTP едет открытым текстом.** Наружу — только за TLS.
