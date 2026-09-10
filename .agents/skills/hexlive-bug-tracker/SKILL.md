---
name: hexlive-bug-tracker
description: Read and mutate HexLive bug reports in the central server tracker, including claiming created/rework reports, comments, handoff, fix commits, status changes, and administrative deletion. Use whenever inspecting or fixing HexLive bugs; do not use BUGS.json as a report queue.
---

# HexLive server bug tracker

The source of truth is `https://flashback.62-146-235-120.sslip.io/api/bugs/v1`.
Use `scripts/bugs.py`; it preserves JSON encoding and surfaces HTTP conflicts.

## Connection and compatibility

Production storage is Flashback PostgreSQL in Singapore. The previous
`https://163-245-204-96.sslip.io/api/bugs/v1` address proxies to the same API,
so already running agents and game clients remain compatible. The retired
SQLite database is a backup, not the source of current reports.

Every request, including GET and report creation, requires a Bearer access key.
The updated CLI reads HEXLIVE_BUG_TOKEN, --token-file, the private skill bug-token,
or ~/.config/hexlive/bug-token. The existing agent key is registered with
created/in_progress/ready_for_test/rework permissions; it cannot set fixed,
archive, delete, or manage keys. Use a separate named key per agent when available.
The player key has all statuses but cannot manage access keys. A distinct admin
key manages keys in Flashback. Never print token files or include secrets in reports.

Only the old New York HTTPS API remains proxied. Singapore legacy URLs and
direct game-server ports return 410; do not retry or restore SQLite writers.
The embedded server implementation has been deleted. Singapore legacy IDs
373–377 moved to 398–402; IDs 378–397 were preserved. Always reload the
Flashback card before editing; old Singapore #373–375 refer to different bugs.
Old clients must send authentication on reads
and creates too. The game creation call is updated in source and requires a new
player build. Do not make an anonymous compatibility exception.

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
   `bugs.py update --fix-commits <sha…>` also uploads `git show` of each SHA
   from the current checkout (§114.4c), so the card shows files and the diff;
   pass `--repo <checkout>` when not running from the repository root, or
   `bugs.py push-commit <sha…>` later. The server has no repository: a
   commit whose patch was never uploaded shows only a GitHub link.
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
python3 .agents/skills/hexlive-bug-tracker/scripts/bugs.py update 123 --status ready_for_test --fix-commits <full-sha>
python3 .agents/skills/hexlive-bug-tracker/scripts/bugs.py push-commit <full-sha>
python3 .agents/skills/hexlive-bug-tracker/scripts/bugs.py backfill
python3 .agents/skills/hexlive-bug-tracker/scripts/bugs.py delete 123
```
