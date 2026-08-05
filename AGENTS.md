# HexLive — Codex agent guide

The canonical project guide is `CLAUDE.md`; follow it for architecture,
tooling, verification, and content-pipeline rules. The canonical behaviour
spec is `spec.md` and must stay in sync with code.

## BUGS.json — the in-game bug tracker (spec §114)

At the start of every bug-fixing session, and whenever the user asks to inspect
or fix bugs, read `BUGS.json` at the repository root.

- The work queue is every report whose `status` is `"created"` or `"rework"`.
  Use its captured `context` (`seed=… tick=… npc=…`) to reproduce the issue.
- After a verified fix, set its `status` to `"fixed"` and append a Russian
  comment to `comments` in this shape:
  `{"whenUtc":"…","author":"codex","text":"<что сделано>"}`.
- Keep the JSON pretty-printed with four-space indentation. Never delete or
  reorder reports, and never change `nextId` except to preserve the value
  written by the game. Deletion is the player's in-game acceptance gesture.
- The running game reloads this file by mtime about every two seconds, so edits
  appear live without restarting.
- Builds resolve the store in this order: `-hexlive-bugs <path>`, repository
  root, then `persistentDataPath`. Also inspect the legacy store at
  `~/Library/Application Support/DefaultCompany/HexLive/BUGS.json`. Merge any
  reports found there into the repository file without reordering existing
  reports (legacy ids start at 1001), then leave its `reports` empty while
  preserving its schema and `nextId`.

