# Singapore release coordination — Sveta

Света координирует по ПРЯМОМУ текущему поручению Толи из лички 2026-10-10 00:56 +07. Толя отдыхает и явно разрешил довести существующими Codex+Claude релиз до playable WebGL на СИНГАПУРЕ: новый matching server binary + новый мир + client + новый low-poly people/clothing catalog, старые assets/meshes исключены из активного релиза. Исправления, сборки bundles/player/server, integration, Singapore deployment/restarts/world replacement разрешены без повторных вопросов (сначала rollback backup; New York НЕ трогать). Продолжаем текущих агентов, без competing writers/Unity. В handoff вижу что ты владелец scales/bounds/paint/content, Claude player/server/audio — сохраните фактическое ownership. Заверши свою часть, согласуй freeze/commit/package и передай Claude через существующий handoff. Держи WEBGL_ASSET_HANDOFF.codex.md актуальным; Claude необходимо получить этот scope/authorization через WEBGL_ASSET_HANDOFF.md, не останавливаться на старом запрете публикации. Создам отдельный RELEASE_COORDINATION_SVETA.md в shared checkout. Ответь там: ownership, blockers, ожидаемый следующий milestone; финально SHA, package hashes, live URL и verification. DoD только реальный live browser gameplay с новым миром/одеждой/правильными масштабами/звуком после gesture, отсутствием missing assets/errors; не только сборка. Света проверяет и докладывает Толе.

## Boundaries
- Read existing handoffs, keep established ownership.
- One Unity owner per checkout; no killing another agent or Editor.
- Shared branch pathspec commits only, preserve unrelated changes, no forced checkout/reset.
- Singapore only: before restart announce exact layer/service and back up current binary/simdata/world/content/client. Existing server unused per owner. Do not change New York or Flashback unrelated services.
- Clean ACTIVE production catalogs/build dependencies: old source art may stay archived; no wholesale deletion of original art needed.
- No credentials in messages/logs. Report blockers/evidence here or handoff; tell_sveta.sh --fyi may notify parent.

## Release checklist
- [x] Content fixes verified and final package hash
- [ ] Player+server compatible committed snapshot and tests
- [ ] Deploy/world rollback backup receipt
- [ ] Singapore release, new world, WebGL content
- [ ] Live browser gameplay: new people/clothing, fitting/scales, no missing meshes, motion
- [ ] Audio after gesture, no critical console/network errors
- [ ] Final URL, commits, receipts, known limits

## Codex acknowledgement — 2026-10-09T18:40:07.610619+00:00
Direct user authorization received in this Codex chat. Singapore release/new matching server/client/new world/content/restarts are authorized after rollback backup; New York excluded. Ownership unchanged: Codex content/scales/bounds/paint and subsequent live-browser verification; Claude player/server/audio/deployment and Unity.

Content frozen at `b993f572673387f4dc7d098ae1d8eaf7cfc36a8a`; test correction `3c8c35be2`; complete handoff `cecfc6f8d`. Package `/Users/shtolyan/hex-girls/webgl-build/packages/webgl-primal-scale-b993f5726.tar.gz`, 241658756 bytes, SHA256 `82e2605f3335ffbd2eaf812a399b17ad0c114a1ece7986b66a4d83a4b92ab58d`. 230/230 HTTP blobs locally verified; 121 People payloads reopened, 23 map contracts passed. Content blocker: none. Release is NOT complete until live browser checks.

Next milestone: Claude finishes matching player/server, records backup and Singapore publication receipts/live URL; Codex verifies actual new-world movement, body/clothing scale, loading/console and post-gesture audio through browser. No competing Unity or execution-file writes.

## Coordinator QA gate — 2026-10-10
Owner delegated all testing and Singapore release completion while resting: do NOT wait for the owner to test a local link or approve the already-authorized deploy. Sveta has an independent Chromium/WebGL2+WebAudio probe and the verified expected 230-record manifest; will test public live render/world/catalog/sounds without touching your browser. Important: anonymous /watch only proves viewing. Before declaring playable, use your EXISTING authenticated browser session to exercise an actual player control/command and preserve sanitized success receipt/screenshot (no tokens/keys/logged auth) so Sveta can independently inspect the evidence. Do not share/extract credentials or disable auth. Backup old world/release first; Singapore only.

Coordinator preservation check: Singapore also hosts Flashback on port4310 (https://flashback.62-146-235-120.sslip.io). Baseline HTTPS200 verified at18:45:54Z. Owner says GAME unused, not that unrelated services may be removed. Preserve existing Flashback/Caddy routes and identity service; back up Caddy config before any scoped route edits, validate/reload safely. Do not replace whole host config with only game rules.

## Codex public content proof — 2026-10-09T19:10:04Z
Public HTTPS registry230 = corrected package exactly, no platformMissing; only Marta/Kshishtof actor records. All230 active server blobs SHA256 verified read-only (266130433 bytes), `/var/lib/hexlive/assets-webgl`. Receipt: `/Users/shtolyan/hex-girls/webgl-build/singapore-live-verification/content-registry-and-server-hashes.json`. Active server release observed:88ee8821b55206cdd66f1d7bcf5d8dcb6cc7d069. Backup observed at `/var/lib/hexlive-archive/20261010-pre-webgl` (old worlds, simdata, config, previous-current). Live gameplay gate remains open: IAB and Claude browser both initially paused at Unity loader near90%; Claude investigating delivery.

## RELEASE QA BLOCKER — Sveta independent PUBLIC browser 2026-10-09 19:12:26Z
Observed real console ERROR at https://play.62-146-235-120.sslip.io/: `[AtomicContent] Нет active record для object/clothing.skirt_primal_1; визуальный fallback запрещён.` Stack: ContentPrefabCache.Request/GetOrRequest -> WorldPropResources.Load -> HexWorldRenderer.CreateObjectView/RenderSnapshot. The exact verified 230-entry manifest HAS `wear/clothing.skirt_primal_1` and has NO `object/clothing.skirt_primal_1` (by design). Possibly an incorrect generic-prop fallback while garment async load is pending, NOT just network slowness. Please Codex TRIAGE and own this client routing defect; coordinate any edit/build with Claude, avoid duplicate fixes. Determine whether transient or blocks visible ground garments; fix misleading missing-record fallback too before final no-missing-assets acceptance. Do not add old assets or weaken manifest expectations merely to hide error. Current viewer has live WSS frames but still loading content. Sveta raw evidence on VPS artifacts/hexlive-singapore-release-20261010/browser-events.jsonl. Owner already authorizes in-scope fixes/rebuild/deploy; no owner prompt needed.

QA follow-up19:13Z: more missing-record errors: object/clothing.dress_primal_color05, object/underwear.briefs_primal_male_panty3, AND object/resource.palm_leaf and object/tool.axe_stone. IMPORTANT the latter two DO exist under exactly those object keys in the verified public manifest. So investigate catalog readiness/async race/cache as well as wear classification; do not assume manifest genuinely lacks them. My Chrome is Linux headless SwiftShader: also saw RenderPass 'Attachment0 created with1 samples but2 requested' and EndRenderPass errors (could be software-GPU-specific; compare your hardware Chrome). Own browser AudioContext running after click but current RMS0 while loading; no sound pass claimed yet. No HTTP404 observed. Raw sources and230 IDs/hashes match expected.

## Claude — Singapore release receipt (2026-10-10 ~02:10 +07)
- Server 62.146.235.120 (`hexlive-server`). Release `/opt/hexlive/releases/88ee8821b55206cdd66f1d7bcf5d8dcb6cc7d069`
  (archive SHA-256 575c90ea…aac16); previous `55e2152ed…` kept as rollback.
- Tests: server 374/374 (clean worktree, Release); full Simulation.Tests in complete checkout per Codex cecfc6f8d.
- Backup/archive: `/var/lib/hexlive-archive/20261010-pre-webgl` — old `worlds/`, legacy `world.sav` + 27 backups,
  players.json, simdata, systemd drop-ins, Caddyfile, previous-current.txt; admin/player/mcp secret hashes unchanged.
- New world: fresh Feud, seed 12345, 4 colonists, NO legacy nika/masha presets (their legacy clothes are not in the
  WebGL catalog). Drop-in `zzz-webgl.conf`; no `--start-paused` (fresh world runs).
- Content: new root `/var/lib/hexlive/assets-webgl`, package `webgl-primal-scale-b993f5726` (SHA 82e2605f…ab58d),
  230/230 published, coverage WebGL 230/230. Old `/var/lib/hexlive/assets` untouched.
- Client: `/var/lib/hexlive-web/releases/88ee8821b…` → `current` (player from execution b993f5726 + a559cdb0d audio,
  full exceptions, FSB audio via hexfsb, 172 MB).
- Caddy: appended `Server/Caddy/singapore-webgl.caddy`, validated, reload. Existing vmi3529459 / flashback / keys intact.
- URLs: game https://play.62-146-235-120.sslip.io/ · admin https://admin.62-146-235-120.sslip.io/ (play host /admin → 404).
- External checks: site 200, wasm br 200, .fsb 200, asset index 200, /watch 101, flashback 200, keys 200.
- Live browser (Claude): menu, connect 1.9 s, world tick ~1600, new People + male outsider render, 0 errors.
- Known limits: first load ~3–4 min on a ~250 KB/s path (57 MB player + ~130 content objects); repeat loads revalidate.
  Audio quality after a559 (PCM SFX, FMOD 2×2048) not yet confirmed by ear.

### Root-cause evidence update19:17Z (Sveta, read-only code/log audit)
FIRST relevant warning at19:12:23.117Z: `[AtomicContent] Реестр недоступен: HTTP 500 Request timeout; используем проверенный локальный кэш.` ALL missing-record errors occur AFTER this warning (first clothing at19:12:26; palm_leaf/axe19:13:22; resource.cloth19:14:53). ContentAssetService.RefreshRegistryRoutine uses 10s SendTextAsync timeout; failure calls PinOfflineRecords, which clears _pinned and keeps only _verified entries. ContentPrefabCache.Request then caches terminal Missing based on TryGetRecord; it only forgives on a later healthy registry refresh. Please investigate recovery/automatic retry and preserving legitimate in-flight/known metadata across transient timeout; public230 manifest itself is correct and reachable. Avoid blindly adding aliases based on earlier symptom. Potential garment generic-fallback is secondary while registry degraded. Raw log timestamps substantiate sequence; current cloud Chrome main-thread/GPU is busy, so callback scheduling may contribute to timeout. Need successful fresh-world load + no permanent missing models after transient registry failure.

## Claude — live QA: audio measured, player-command limit (2026-10-10 ~02:25 +07)
Freeze: deployed server/site `88ee8821b55206cdd66f1d7bcf5d8dcb6cc7d069`, content `b993f5726`; branch head at write `c9403cd45fa23280dd845e3c02a5999ce2f5949c`.
Screenshot: `/Users/shtolyan/hex-girls/webgl-build/release-evidence/20261010-singapore/live-world-claude.jpg` (public play host, in world, tick ~4200, no secrets on screen).

**Audio — objective, not by ear.** Before Unity started, the page's `AudioNode.prototype.connect` was wrapped so that
every node connected to `AudioDestinationNode` also feeds an `AnalyserNode` (fftSize 2048); RMS/peak read every 40 ms.
- Before gesture: 0 taps (AudioContext suspended, expected). After one click: log
  `[Music] web audio resumed after the first player gesture`, context `running`, 44100 Hz, 1 tap.
- Menu, 10 s: 224 windows, **0 silent**, RMS p10 0.050 / median 0.066 (music), page 60 fps, max frame gap 21 ms.
- In world, 15 s: 324 windows, **0 silent**, RMS p10 0.020 / median 0.032; FMOD/ERR_FORMAT errors 0.
- In world the pane became HIDDEN (Electron throttles rAF to ~1 Hz although visibilityState=visible): the signal still had
  no gaps at 1 fps — the AudioWorklet mixer is independent of the frame, i.e. the a559 change behaves as intended.
- Limits: proves non-silent continuous output and absence of dropouts at window resolution (~46 ms); does NOT judge
  timbre, mix balance, 3D panning by ear, voice-line lipsync or footstep doubling (listener fix verified in code, not by ear).
  In-world FPS on a visible pane not measured in this run (menu: 60 fps).

**Player command — NOT done, honestly.** This browser session is anonymous: log `стартовый выбор: npc=1 ЧУЖАЯ/нет прав;
приказы запрещены`. No existing authorized session exists here; entering a player token/closed-test key into a production
page is outside what I may do, and auth was not weakened. Needs Tolya (or a session he authorizes himself).

**Sveta's registry-timeout blocker:** not reproduced in this session (no `[AtomicContent]`/registry warnings; registry
answered in time) — intermittent, consistent with her root cause. Codex owns the ContentAssetService fix; I do not touch
those files and will rebuild the Player only from Codex's SHA.
