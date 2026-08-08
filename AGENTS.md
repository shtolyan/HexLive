# HexLive — Codex agent guide

The canonical project guide is `CLAUDE.md`; follow it for architecture,
tooling, verification, and content-pipeline rules. The canonical behaviour
spec is `spec.md` and must stay in sync with code.

## BUGS.json — the in-game bug tracker (spec §114)

At the start of every bug-fixing session, and whenever the user asks to inspect
or fix bugs, read `BUGS.json` at the repository root.

- The work queue is every report whose `status` is `"created"` or `"rework"`.
  Before beginning work on one, immediately set its status to `"in_progress"`
  and append a Russian `codex` comment saying that work has been taken. Use its
  captured `context` (`seed=… tick=… npc=…`) to reproduce the issue.
- When implementation is complete, set the report to `"ready_for_test"` and
  append a Russian `codex` comment describing what was done and that it is
  ready for the player's test. Do not set `"fixed"` or archive a report: only
  the player confirms `ready_for_test → fixed`, sends it back to `"rework"`,
  or archives a confirmed fix.
- On taking a report, preserve or set `assignedAgent` to the stable agent task
  name and keep a concise Russian `agentHandoff` (diagnosis, files changed,
  repro and remaining risk). A `rework` keeps both fields. The orchestrator
  must route it to that live agent first; when its session is gone, a new agent
  must read the saved handoff and all comments before continuing.
- Every implementation commit for a bug is atomic: subject
  `fix(bug-<id>): <кратко>`, trailer `Bug: #<id>`. Append its full SHA to
  `fixCommits` immediately — never replace earlier SHAs. The final commit must
  exist before `ready_for_test`; `fixCommit` is legacy compatibility only.
  Stage only the bug's files; if unrelated dirty changes overlap, stop and ask
  the player rather than absorbing them into the fix commit.
- Status lifecycle is `created → in_progress → ready_for_test → fixed`, with
  `ready_for_test → rework → in_progress` for a failed test. `archived` is a
  separate history flag for a confirmed `fixed` report, not deletion.
- Keep the JSON pretty-printed with four-space indentation. Never delete or
  reorder reports, and never change `nextId` except to preserve the value
  written by the game.
- The running game reloads this file by mtime about every two seconds, so edits
  appear live without restarting.
- Builds resolve the store in this order: `-hexlive-bugs <path>`, repository
  root, then `persistentDataPath`. Also inspect the legacy store at
  `~/Library/Application Support/DefaultCompany/HexLive/BUGS.json`. Merge any
  reports found there into the repository file without reordering existing
  reports (legacy ids start at 1001), then leave its `reports` empty while
  preserving its schema and `nextId`.
