---
name: hexlive-bug-tracker
description: Read and mutate HexLive bug reports in the central server tracker, including claiming created/rework reports, comments, handoff, fix commits, status changes, and administrative deletion. Use whenever inspecting or fixing HexLive bugs; do not use BUGS.json as a report queue.
---

# HexLive server bug tracker

The source of truth is `https://vmi3529459.contaboserver.net/api/bugs/v1`.
Use `scripts/bugs.py`; it preserves JSON encoding and surfaces HTTP conflicts.

## Authentication

Mutations require the agent Bearer token. Prefer `HEXLIVE_BUG_TOKEN`; otherwise
pass `--token-file`. On the server the token is
`/var/lib/hexlive/hexlive-bugs-token.txt`. Never print or commit it.

Reads and new player reports are public. Status changes, agent fields,
comments authored by an agent, and deletion are authenticated.

## Workflow

1. Run `scripts/bugs.py queue` and inspect every `created` or `rework` report,
   including `context`, `comments`, and `agentHandoff`.
2. Claim before code work with one update containing `status=in_progress`,
   `assignedAgent=<stable task name>`, a concise Russian `agentHandoff`, and
   then append a Russian `codex` comment.
3. Keep the report in progress while diagnosing. Use `expectedRevision` when
   updating a report read earlier; HTTP 409 means reload before retrying.
4. Make the required atomic commit `fix(bug-<id>): ...` with trailer
   `Bug: #<id>`. Append, never replace, its full SHA in `fixCommits`.
5. Only after the commit exists, set `ready_for_test`, update the Russian
   handoff, and append a Russian ready-for-player-test comment.

Never mark `fixed` or archive on behalf of the player. Delete only when the
player explicitly asks; server deletion is permanent. `BUGS.json` is only the
Unity MCP lease coordination file and a retired migration source.

Useful commands:

```bash
python3 .agents/skills/hexlive-bug-tracker/scripts/bugs.py queue
python3 .agents/skills/hexlive-bug-tracker/scripts/bugs.py get 123
python3 .agents/skills/hexlive-bug-tracker/scripts/bugs.py update 123 --status in_progress --assigned-agent /root --handoff "Взят в работу"
python3 .agents/skills/hexlive-bug-tracker/scripts/bugs.py comment 123 --author codex --text "Взял в работу."
python3 .agents/skills/hexlive-bug-tracker/scripts/bugs.py delete 123
```
