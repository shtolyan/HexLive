# HexLive — Codex agent guide

The canonical project guide is `CLAUDE.md`; follow it for architecture,
tooling, verification, and content-pipeline rules. The canonical behaviour
spec is `Spec/<N>.md` — one file per section — and must stay in sync with code.

`§N` resolves to `Spec/N.md` mechanically: seeing `§105.14` in a C# comment,
open `Spec/105.md` — no grep. Sub-points live inside their section's file.
The root `spec.md` is a GENERATED index (`python3 Tools/spec_index.py`); read
it whole, never edit it by hand. A new section is created only with
`python3 Tools/spec_new.py "Название"`, which allocates the next free number —
picking one by eye is how §84 ended up holding two different topics. Section
numbers never change: 3773 C# references depend on them. `SpecStructureGate`
in `dotnet test` guards all of this.

## Mandatory macOS application signing

For every agent, task, checkout and builder, follow `Tools/MACOS_SIGNING.md`.
Publish the macOS client only through `Tools/build_release.py` and Agent Studio
through `Tools/package_agent_studio.py`. Both require the shared
`Tools/macos_signing.py` preflight and certificate signing before publication.
This includes development and `--release` clients. Do not bypass a failed
preflight/signature check with ad-hoc signing, a different certificate, or a
manual latest-link update. Keep bundle IDs `com.juilcylove.hexgirls` and
`com.hexlive.agentstudio` stable. The configured identity is shared by all tasks
under the same macOS user via `~/.config/hexlive/macos-signing-identity`;
another builder needs the same certificate/private key in its own Keychain.
Private keys must never enter the repository. A new macOS app publisher must
use the same mandatory helper and verification. Local Apple Development signing
does not mean Developer ID notarization or automatic microphone/Keychain consent.
The standalone `Tools/build_hut_test.py` client follows the same signing policy.

## Furniture art and hex placement

Before editing a furniture Blender source/FBX, pivot, axes, scale, footprint,
wall alignment, six-way yaw, construction hierarchy, or test-versus-production
placement, use the project skill at
`.agents/skills/hexlive-furniture-authoring/SKILL.md`.

Do not repair a crooked asset with an asset-specific runtime angle or scene
transform. Normalize its source/export basis, keep placement in committed data,
and verify the test fixture through the same factory/loading path as the game.

## Unity UI Toolkit

Before inspecting, editing, or generating Unity interface layouts, controls,
styles, inventory screens, HUDs, `UIDocument`/`PanelSettings` setup, UXML, USS,
Painter2D visuals, or UI pointer interactions, use the official Unity router at
`.agents/skills/ui/SKILL.md`. HexLive's runtime interface uses UI Toolkit, so
route that work to `.agents/skills/ui-uitk/SKILL.md` and follow its relevant
references before changing code or assets.

The UI skills do not grant permission to call Unity MCP. The single-owner lease
below remains mandatory for every Unity MCP operation, including read-only
inspection and validation.

## Unity MCP single-owner lease (mandatory)

`BUGS.json` is also the source of truth for the one allowed Unity MCP user.
Before **any** Unity MCP tool/resource call (including discovery, read-only
inspection, console reads, screenshots, tests, or mutations), atomically acquire
the top-level `unityMcpLease` with:

```bash
python3 Tools/unity_mcp_lease.py acquire --agent <stable-agent-task-name> --task "<short purpose>"
```

- A successful command records `status:"busy"`, `ownerAgent`, `task`, and UTC
  timestamps. Only that exact owner may then call Unity MCP. Re-run `heartbeat`
  during long work and `release --agent <name>` immediately after the last MCP
  call (including post-timeout polling), on failure, or before waiting for the
  player.
- If acquisition reports another owner, **do not make even a probe MCP call and
  do not edit/release/steal the lease**. Read the owner and task from `BUGS.json`,
  contact that agent through the orchestrator, and wait for `status:"free"`.
  An abandoned lease is cleared only by its owner or on explicit player direction.
- Check with `python3 Tools/unity_mcp_lease.py status`. Direct hand-editing is
  not an acquisition: the CLI's lock makes the free→busy transition atomic when
  agents race. Ordinary filesystem/code work that never calls Unity MCP needs no
  lease.

## Server bug tracker (spec §114)

At the start of every bug-fixing session, and whenever the user asks to inspect
or fix bugs, use `.agents/skills/hexlive-bug-tracker/SKILL.md`. The source of
truth is the production server SQLite database exposed at `/api/bugs/v1`.

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
- Do not edit `BUGS.json` for report work. It is a retired one-shot migration
  source. The game and agents read and mutate reports through authenticated
  HTTP; the web admin uses the same database.
- `BUGS.json` remains only the coordination file for the Unity MCP lease below.
  Never add reports back to it or treat its stale `reports` array as a queue.
