# HexLive — Codex agent guide

The canonical project guide is `CLAUDE.md`; follow it for architecture,
tooling, verification, and content-pipeline rules. The canonical behaviour
spec is `Spec/<N>.md` — one file per section — and must stay in sync with code.

## Production changes require exact authorization

- Treat the requested layer as a hard scope boundary. A request to configure an
  API key, SSH access, DNS, TLS, an environment file, a reverse proxy or one
  service authorizes only that configuration change and its minimum necessary
  service reload/restart. It does **not** authorize deploying a new game-server
  binary, changing `/opt/hexlive/current`, replacing simdata or content, changing
  the wire protocol, rebuilding a client, or publishing a client build.
- Never infer a server deployment from “make it work”, “finish the setup”, access
  to root, or the existence of a newer commit. Deploy/update/redeploy a game
  server only when the player explicitly names that server and explicitly asks
  to deploy or update its game-server version in the current task.
- Singapore and New York are independent authorization targets. Permission to
  change one never applies to the other, and permission for one deployment does
  not remain open-ended for later turns.
- If a requested configuration needs a newer server binary, stop before changing
  production. Report the installed/new versions, the client-compatibility impact
  and the exact deployment that would be required, then wait for explicit
  approval. Never force an old client to require replacement as a side effect of
  a configuration-only task.
- Before any production mutation, state in commentary the exact server, layer
  (`configuration`, `service`, `server binary`, `content`, or `client`) and the
  service(s) that will restart. If that scope is broader than the player's words,
  do not perform it.

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

UI work follows the Unity CLI rule below; UI skills do not authorize MCP.

## Unity CLI only — player instruction, 2026-10-09

Use Unity CLI for Unity Editor work. Do not use Unity MCP, including tool
or resource discovery, read-only probes, console reads, screenshots, or tests.
Do not install or invoke the retired `unity-mcp-orchestrator` skill. Older
MCP instructions in skills or checkouts do not override this rule.

The installed macOS CLI is `/opt/homebrew/bin/unity` (Homebrew `unity-cli`).
Read the `unity-cli` skill; use `unity command --project-path <checkout>` to
explicitly target the agreed Editor. `unity build`, `unity run`, and
`unity test` may launch an Editor and are not safe discovery commands.
Do not invoke `unity mcp` as an alternative transport.

Before using Unity CLI, coordinate with the agent owning the Editor or build.
Do not interrupt their build, restart their Editor, or launch a competing
Editor. A busy/unavailable Editor is not permission to fall back to MCP.
Filesystem-only preparation may continue in the agreed shared checkout.
The old `unityMcpLease` field and helper are historical, not authorization
for Unity access.

For the current WebGL work, coordinate with Claude in `claude/webgl-port`
at `/Volumes/ORICO/HexLive-webgl`; consult `WEBGL_ASSET_HANDOFF.md` when present.
New people/clothing assets are prepared there alongside the WebGL port.
Do not build or publish AssetBundles until the player explicitly commands it.

## Server bug tracker (spec §114)

At the start of every bug-fixing session, and whenever the user asks to inspect
or fix bugs, use `.agents/skills/hexlive-bug-tracker/SKILL.md`. The source of
truth is Flashback PostgreSQL in Singapore, exposed at
`https://flashback.62-146-235-120.sslip.io/api/bugs/v1`. The old production
New York HTTPS `/api/bugs/v1` route proxies to this same API. Direct game-server
routes and the old Singapore hostname return 410. The embedded SQLite tracker
has been removed; archived snapshots are not writable runtime stores.

- All agents on a laptop use its device key in `~/.config/hexlive/bug-token`
  (Windows: `%USERPROFILE%\.config\hexlive\bug-token`). Never prescribe a
  person's token in shared instructions. Run `bugs.py whoami` before writes;
  verify the displayed key name belongs to this device. Missing credentials
  must stop work, never fall back to another key or a repository token.
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
  source. The game and agents read and mutate reports through the same HTTP API;
  all requests require a Flashback Bearer access key. The web UI uses the
  same PostgreSQL database.
- `BUGS.json` retains historical coordination data only; Unity MCP is retired.
  Never add reports back to it or treat its stale `reports` array as a queue.
