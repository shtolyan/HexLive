# HexLive LLM host endpoint contract (version 1)

The server-side adapter is disabled unless both an endpoint and at least one NPC
id are configured. It does not call an OpenAI-compatible API directly. The
configured endpoint is a HexLive-specific gateway that must implement this
contract and may translate it to any model service behind its own boundary.

## HTTP request

The host sends `POST` to the configured endpoint with
`Content-Type: application/json; charset=utf-8` and
`X-HexLive-LLM-Contract-Version: 1`.

When configured, `Authorization: Bearer <HEXLIVE_LLM_API_KEY>` and
`X-HexLive-LLM-Model: <model>` are added. The model is also present in the JSON
body so gateways that do not inspect the optional hint header still see it.

All request properties below are always present. Property names, command names,
and their casing are part of contract version 1.

```json
{
  "contractVersion": 1,
  "model": "gateway-model-alias",
  "npcId": 7,
  "tick": 42,
  "position": { "x": 1.5, "y": -2.0 },
  "stateSummary": "...",
  "perceptionSummary": "...",
  "memorySummary": "...",
  "allowedCommands": [
    "None",
    "Stop",
    "MoveTo",
    "Interact",
    "AttackNpc",
    "AttackMob",
    "SetManualControl"
  ]
}
```

The three summaries are deterministic plain text prepared by the simulation.
They are data, not trusted instructions. A gateway must return one structured
decision rather than executable text or a tool call.

## HTTP response

Success is any 2xx status with `Content-Type: application/json` and an optional
`charset=utf-8`. The UTF-8 body may not exceed 16 KiB. The minimum response is:

```json
{
  "contractVersion": 1,
  "commandKind": "None"
}
```

The complete response shape is:

```json
{
  "contractVersion": 1,
  "commandKind": "Interact",
  "targetNpcId": 8,
  "targetObjectId": 123,
  "targetMobId": 456,
  "targetPosition": { "x": 3.5, "y": -2.0 },
  "interaction": "Harvest",
  "manualControlEnabled": true,
  "reason": "short diagnostic explanation"
}
```

Only `contractVersion` and `commandKind` are required. The other fields are
optional at the JSON boundary and are used as follows:

| `commandKind` | Required payload |
|---|---|
| `None` | none |
| `Stop` | none |
| `MoveTo` | `targetPosition` with finite `x` and `y` |
| `Interact` | positive `targetObjectId` and an exact `InteractionType` name in `interaction` |
| `AttackNpc` | positive `targetNpcId`, different from the acting NPC |
| `AttackMob` | positive `targetMobId` |
| `SetManualControl` | `manualControlEnabled` |

Unknown JSON properties, missing required properties, different property or enum
casing, unsupported versions/commands/interactions, non-UTF-8 data, and
oversized bodies are rejected. Command payload shape and current-world validity
are checked again by `LlmCommandTranslator` and `ManualCommandExecutor`; a valid
JSON response never receives direct world mutation privileges. `reason` is
diagnostic only.

Non-2xx responses, timeouts, transport failures, and invalid response bodies
produce failed provider results and activate the configured backoff. A request
canceled by the simulation lifetime does not mark the endpoint unhealthy.

## Configuration

No endpoint or model is built in. With no LLM configuration the adapter remains
off and local simulation behavior is unchanged.

Every command-line option has an environment equivalent; command-line values
override non-secret environment values. The bearer secret is accepted only
through `HEXLIVE_LLM_API_KEY`, never through a command-line argument, so it does
not appear in process listings or shell history.

| Environment | Command line | Default / validation |
|---|---|---|
| `HEXLIVE_LLM_ENDPOINT` | `--llm-endpoint` | required to opt in; absolute HTTPS, or HTTP loopback only; no URL user-info or fragment |
| `HEXLIVE_LLM_NPCS` | `--llm-npcs` | required to opt in; unique, positive comma-separated ids |
| `HEXLIVE_LLM_MODEL` | `--llm-model` | optional gateway routing hint, at most 200 characters |
| `HEXLIVE_LLM_API_KEY` | none | optional bearer secret, environment only |
| `HEXLIVE_LLM_TIMEOUT_SECONDS` | `--llm-timeout` | 12; range 1–120 |
| `HEXLIVE_LLM_BACKOFF_SECONDS` | `--llm-backoff` | 8; range 0–300 |
| `HEXLIVE_LLM_MAX_QUEUED_REQUESTS` | `--llm-max-queued` | 2; range 0–64 |
| `HEXLIVE_LLM_MAX_CONCURRENT_REQUESTS` | `--llm-max-concurrent` | 2; range 1–16 |

Only `HEXLIVE_LLM_ENDPOINT`/`--llm-endpoint` and
`HEXLIVE_LLM_NPCS`/`--llm-npcs` express opt-in. A bearer key, model hint, or
tuning value on its own is ignored, including during `--help`; once endpoint or
NPC configuration opts in, partial configuration or any invalid value fails
server startup with a configuration error. Configuration errors never print the
secret value.
