# Singapore release coordination — Sveta

## CURRENT OVERRIDE — player requested backpack overlap restored (2026-10-10)

**Include corrective commit `d42be421673599ceb719728fa6dd357d6802f8a7` before publishing the combined Player.** Direct latest instruction from Tolya: remove ONLY automatic clearance outside the backpack; he wants a partially concealed weapon/visible handle. Previous requirement and render comparison showing a tool outside the backpack are superseded.

Removed bag geometry baking, bag-dependent remount and BodyBones.TorsoBag. Kept the metre anchor (.01 bug fix), tool sizing, async config/held-garment retry, either-hand duplicate suppression. Spec169 and the existing pack regression now require one unchanged slot through equip/remove.

Source fix committed. Two focused pose/fit contracts passed2/2; corrected Unity pack regression is not rerun because Claude owns `v5-perf-build`. **Execution checkout deliberately untouched during the build**: Claude must sync NpcActorView.cs, BodyBones.cs and PeoplePropAttachmentRuntimeTests.cs from this commit after his current build stops, then validate/rebuild the release candidate. Do not publish the previous bag-clearance behavior. Prior5/5 XML and pack images remain historical evidence for the superseded commit, not proof of the reverted variant. No content/server/world changes needed.

Native Claude UI message attempts were interrupted by user control; shared handoffs and Sveta are the delivery route. Codex does not interrupt the active Unity build or edit its execution files.


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

## Codex — recovery fix READY / Unity released (2026-10-09 19:30Z)
Fix SHA `1b24d940cc3b266629ef2dc5ff4f0fbb62e74f78` in shared branch. Client-only; content archive remains `webgl-primal-scale-b993f5726.tar.gz`, SHA256 `82e2605f3335ffbd2eaf812a399b17ad0c114a1ece7986b66a4d83a4b92ab58d`; no server/world/content rebuild required for this fix.

Root cause verified: index failure removed known metadata for not-yet-verified payloads; subsequent cache lookup stored Missing. Fix retains known records, keeps cold-start callbacks pending, retries index with one timer (2/4/8/16/30s), cancels stale endpoint retries, defers Missing while registry degraded, routes sim clothing to wear before payload arrival. Hash/size validation and verified offline revision fallback remain in force.

Validation: 8/8 Unity graphics PlayMode recovery tests; 3/3 existing transport/terminal contract checks in complete checkout. Sparse source check had only its known absent StreamingAssets files failure; complete checkout passed. Report committed at `Assets/HexLiveContent/People/Validation/registry-recovery-playmode.xml`. Unity CLI run finished, UNITY_OWNER released. Three production C# files already copied and compiled in execution; preserve other staged/LFS files. Claude may now freeze/build/redeploy CLIENT ONLY to Singapore with the existing rollback process and fresh live QA. Codex will not write execution during your build.

Remaining acceptance: independent fresh-browser content recovery/no missing models, hardware-rendered gameplay proof; authenticated player command is still unproven because Claude session anonymous. Sveta notified to use an existing authorized session if available; do not ask sleeping owner to test or weaken auth. Audio signal after gesture objectively passed in Claude current client (details above); rerun smoke after new Player.

## Codex — follow-up QA triage (2026-10-09 20:00Z)
Latest queued Sveta message repeats the 19:12–19:17Z evidence. Read both handoffs and coordination fresh: this is the defect addressed by `1b24d940c`, not evidence of recurrence in v3. Public client has not yet been replaced; Claude v3 still linking, UNITY_OWNER=Claude. Keep release acceptance OPEN and test a fresh client after publication.

MSAA source audit (no edits): PC/Mobile URP m_MSAA=1, quality antiAliasing=0; CharacterDollStage, PortraitStage and NpcPortraitCache request antiAliasing=2 on their offscreen RenderTextures. This is a plausible location for SwiftShader sample-count mismatch, not a proven diagnosis. Need compare actual live game logs/portraits on Mac hardware with the Linux software-GPU report before changing it. Codex will test new client in Chrome after build; no competing Unity and no renderer-file writes during freeze. Claude remains player/rendering release owner.

## Sveta — authenticated QA availability check (2026-10-10, after v3 compression notice)
Read current coordination receipt. No v3 publication receipt yet in this document; do not treat old-client findings as v3 regressions. OpenClaw browser profiles inspected: work/openclaw/user/chrome all reported running=false, tabCount=0; no existing authorized gameplay session accessible via this browser channel was established. This does NOT prove no authorized session exists elsewhere on the Mac. No credentials read/copied, no auth changes, no player command executed, no acceptance pass claimed. Claude remains sole deploy writer; Codex continues independent post-update QA. Authenticated player command remains an explicit unverified gate, not a waived test. Please append exact v3 deployment/version receipt when ready; preserve browser ownership.

## Codex — v3 publication independently verified (2026-10-09 20:15Z)
Live client `/var/lib/hexlive-web/current` now points to `fa93cfa1955992e80e99fab5b278fada918bde69`. Claude upload receipt: archive 161141283 bytes, SHA256 `7341f743dd0455408e7e4d3bb569fca3a543b11bcf1c957ef9ad1b34c086efe5`; previous site `88ee8821b...` retained for rollback. Server remains `/opt/hexlive/releases/88ee8821b55206cdd66f1d7bcf5d8dcb6cc7d069`, content remains `b993f5726` package / 230 records. Codex read-only SSH independently matched SHA256 of all four deployed v3 startup files to the local candidate. Receipt: `/Users/shtolyan/hex-girls/webgl-build/singapore-live-verification/client-v3-startup-hashes.json`.

Fresh Chrome tab at https://play.62-146-235-120.sslip.io/ confirmed `Build/WebGL-v3.loader.js`; menu loaded, Continue entered content loading, post-click audio resume logged at20:15:36Z. No AtomicContent errors so far; world/portraits still loading, not a completed gameplay pass. Two early generic "Could not establish connection. Receiving end does not exist" errors have no game stack in the browser capture; origin unclassified, do not silently claim zero console errors. Hardware/SwiftShader comparison and player-command gate remain open.

## Claude — client v3 published + hardware QA (2026-10-10 ~03:20 +07)
- Client package: `webgl-site-fa93cfa1955992e80e99fab5b278fada918bde69.tar.gz`, **SHA-256 `7341f743dd0455408e7e4d3bb569fca3a543b11bcf1c957ef9ad1b34c086efe5`**, 161141283 bytes. Player built from execution =
  b993f5726 content code + a559cdb0d audio + Codex `1b24d940c` registry recovery (4 files hash-verified before build);
  Unity 34.6 min, 0 errors; FSB 2872 sounds. Site only: `/var/lib/hexlive-web/current` →
  `releases/fa93cfa1955992e80e99fab5b278fada918bde69` (previous `releases/88ee8821b…` kept for rollback). Server/world/content unchanged, no restart.
- Renderer (same session, WEBGL_debug_renderer_info): **ANGLE (Apple, ANGLE Metal Renderer: Apple M1)**, vendor
  Google Inc. (Apple), Chromium 152 in the Claude app — hardware, not SwiftShader.
- Console in world (151 lines): `[AtomicContent]`/active-record/registry errors **0**; GL/attachment/MSAA errors **0**;
  exceptions 0. Warnings: `FaceExpressionCatalog 'CuteFun' not found` and `§120 no model for architecture.roof.palm`
  (both in Codex scope, fix pending). Portraits baked for npc 1–3 and visible in roster/relationships.
- Audio after gesture: context running; menu RMS 0.074; in world 15 s, 316 windows, **0 silent**, RMS p10 0.035 / median 0.045.
- FPS on a visible pane: not measurable here — the pane was hidden (visibilityState=hidden, rAF ~1 Hz). Not claimed.
- Screenshot: `/Users/shtolyan/hex-girls/webgl-build/release-evidence/20261010-singapore/live-world-v3-claude.jpg`.
- Player command: still not done — anonymous session (`приказы запрещены`), auth not bypassed.
- Holding further Player builds until Codex's face-catalog SHA.

## Codex — face/roof follow-up READY, Unity released (2026-10-09 20:31Z)
Fix `5efe07ea011a0f93e1af8a13d77fe5b6ba7b6f88` committed. Unity CLI PlayMode2/2 passed (final run20:30:23–24Z), report `Assets/HexLiveContent/People/Validation/face-expression-playmode.xml`. Tested late real blendshape application to an existing actor, stable custom bindings, existing conversation acquiring its expression, and immediate cached loading. Three production files are already copied/compiled in execution and SHA256-equal to source: `Wearing/FaceExpressionCatalog.cs`, `Wearing/NpcFaceAnimator.cs`, `Environment/ArchitectureModuleView.cs`. Face catalog appends late recipes and existing animators refresh; architecture warns only after terminal content failure. No model, transform, server or bundle changes.

UNITY_OWNER released after CLI exit0; Claude explicitly notified via existing task to build/publish client-only v4. Keep existing content package SHA256 `82e2605f3335ffbd2eaf812a399b17ad0c114a1ece7986b66a4d83a4b92ab58d`. Codex stops execution writes during Claude freeze/build. Next milestone: v4 package/deploy receipt and fresh-browser face/roof/registry QA. Authenticated gameplay remains unverified, not waived. New York untouched.

## Codex — complete PUBLIC payload verification (2026-10-09 20:37Z)
Fetched the public WebGL index and independently streamed **all 230 payload bodies** over HTTPS from `https://play.62-146-235-120.sslip.io/api/assets/v1/blobs/<sha>`, not SSH-local disk. **230/230 match expected SHA-256 and exact byte length**, 266130433 bytes total; 0 failed, index revision230, no platformMissing, exact object-key set equals the b993f5726 package manifest. Includes the previously reported axe/palm leaf and all wear entries. Downloaded20:35:08–20:37:26Z with3 workers. Receipt `/Users/shtolyan/hex-girls/webgl-build/singapore-live-verification/public-all-230-payloads.json`. This closes public archive integrity/availability only, not the authenticated gameplay gate. Claude v4 build still running; no competing editor or production writer.

## Codex — FINAL content/client-fix receipt, acceptance still OPEN (2026-10-09 21:00Z)
- Live URL: https://play.62-146-235-120.sslip.io/ . Client release `9e1a9215c8e1be63969b030cbc0da499da79548b`, includes registry fix `1b24d940cc3b266629ef2dc5ff4f0fbb62e74f78` and face/roof fix `5efe07ea011a0f93e1af8a13d77fe5b6ba7b6f88`. Focused Unity PlayMode8/8 +2/2 passed.
- v4 site archive `webgl-site-9e1a9215c8e1be63969b030cbc0da499da79548b.tar.gz`: **161177437 bytes**, SHA256 **`881171b4a68ba8a466c444ab67dd90211b700abefca3178e19c88b6f9dc34354`** (Codex independently read remote archive hash). All4 deployed startup files SHA256-match the local v4 candidate. Receipt `.../singapore-live-verification/client-v4-startup-hashes.json`.
- Content unchanged: `webgl-primal-scale-b993f5726.tar.gz`, SHA256 **`82e2605f3335ffbd2eaf812a399b17ad0c114a1ece7986b66a4d83a4b92ab58d`**,241658756 bytes. All230 PUBLIC payload bodies verified above. Server/world unchanged during v3/v4 client updates; rollback site retained, New York untouched.
- Independent Chrome v4: scripts confirmed, gesture20:56:04Z, world curtain removed20:56:21Z (~17s). Selected/followed Nadia, Sonya, Tessa: moving and lying bodies visible, Primal garments/backpacks proportionate, awake portraits visible. AtomicContent0, face-catalog warnings0, roof-missing warnings0, MSAA/attachment errors0, game exceptions0. Audio resume logged after gesture; Codex did not measure signal. Two generic receiver connection errors precede Unity startup and have no game stack; origin unclassified, not silently counted as a zero-console-error pass. Normal pre-ready watchdog/queue warnings resolve with the curtain.
- Receipt `/Users/shtolyan/hex-girls/webgl-build/singapore-live-verification/chrome-v4-world-observation.json`. Hardware comparison: Claude Apple M1 Metal passed v3; his v4 audio/hardware receipt follows separately. SwiftShader rerun remains unverified here.
- **Remaining acceptance blocker:** an actual authenticated player command has not been executed. Both available QA sessions report guest/«приказы запрещены». UI selection/follow and observing autonomous NPC movement are not substitutes. No credentials extracted, no auth changes. Sveta notified; no request to wake Tolya or reapprove the authorized release.
- Ownership: Codex code/content fix and public integrity/live-render checks complete; Claude player/audio/deployment owner, Sveta independent acceptance coordinator. No Unity or execution writes pending from Codex. Full playable-release DoD is NOT declared complete.

## Claude — client v4 published + hardware QA (2026-10-10 ~04:00 +07)
- Client `webgl-site-9e1a9215c8e1be63969b030cbc0da499da79548b.tar.gz`, **SHA-256 `881171b4a68ba8a466c444ab67dd90211b700abefca3178e19c88b6f9dc34354`**, 161177437 bytes (matches Codex's remote check). Player from execution =
  previous v3 sources + Codex `5efe07ea0` (FaceExpressionCatalog, NpcFaceAnimator, ArchitectureModuleView; 8 files
  hash-verified before build); Unity 17.1 min, 0 errors; FSB 2872. `current` → `releases/9e1a9215c8e1be63969b030cbc0da499da79548b`; previous
  `releases/fa93cfa19…` (v3) and `88ee8821b…` (v1) kept. Server/world/content unchanged; hexlive+caddy active.
- Renderer: ANGLE Metal, Apple M1 (hardware). Console after curtain (152 lines): AtomicContent 0, FaceExpressionCatalog/
  CuteFun 0, roof-no-model 0, GL/attachment/MSAA 0, exceptions 0; portraits baked 2 (third colonist unconscious).
- Audio: context running after gesture, menu RMS 0.055; in world 15 s, 315 windows, **0 silent**, median RMS 0.030.
- Load in my session: curtain ~150 s after Continue on a cold cache for v4 content queue (Codex warm Chrome ~17 s);
  first-load speed remains the next optimisation target, not a blocker.
- Screenshot: `/Users/shtolyan/hex-girls/webgl-build/release-evidence/20261010-singapore/live-world-v4-claude.jpg`.
- **Not accepted as fully playable:** authenticated player command unverified (both agents' sessions are guests,
  `приказы запрещены`). Gate sent to Sveta below. FPS on a visible pane not measured (pane hidden).

### Open gate — authenticated command (for Sveta / Tolya)
In a browser session that ALREADY holds a valid player identity for Singapore (no token pasted by an agent):
1. open https://play.62-146-235-120.sslip.io/ → click once (audio) → Continue;
2. pick a colonist of your own camp in the roster; the startup log line must NOT say `ЧУЖАЯ/нет прав`;
3. give one order (e.g. move to a tile / gather) and see the colonist start it within a few seconds;
4. record time, colonist, order and a screenshot; any `ManualOrderInterrupted`/rejection text verbatim.
Pass = order accepted and executed. Until then the release is "deployed, guest-verified".

## Codex — attachments READY / Unity released (2026-10-09T21:22:01+00:00)
Fix SHA **`5bd898a03ac827cd823b442679c3933988aadf27`**. Ownership: Codex attachment/content; Claude player/performance/audio/deploy. Two production files only: `Assets/HexLive/UnityPresentation/Wearing/NpcActorView.cs` and `BodyBones.cs`. Execution copies SHA256-equal and compiled; UNITY_OWNER removed after Unity CLI exit0. No further Codex execution writes; Claude may freeze and build the combined Player.

Root cause: raw chestUpper inherited FBX .01, reducing the authored back depth from 0.0838588 world metres to 0.000838588. Back now uses PeopleAppearance.PropAnchor just like both hands. It waits for GearConfig, suppresses duplication in either hand, and places the tool outside an equipped Bags/Chest mesh with 0.015 actor-metre clearance. Bag geometry is baked **only on item/bag change** (BakeMesh(true) preserves imported scale), never per frame. Removal uses reference identity so a destroyed Unity bag also invalidates the cached mount. Held garments retry when their asynchronous mesh was initially unavailable.

Validation: **Unity graphics PlayMode5/5**; actual released inventory **40 portable IDs /340 measurements** across Marta/Kshishtof, both main/off hands and10 tools on back. **18/18 focused dotnet contracts** in complete checkout; sparse-source attempt failed for unavailable asset files, not treated as a pass. All six body/pack combinations rendered and reviewed in run pose, existing mount reseats on pack equip/remove; same size and actor-metre offset contracts checked. Reports: `Assets/HexLiveContent/People/Validation/prop-attachment-playmode.xml` (environment/command-line removed), `prop-attachment-audit.json`, `Review/attachment-*.png`; comparison `Review/attachment-comparison.png`.

No content/server/world rebuild needed. Existing content tar SHA256 stays `82e2605f3335ffbd2eaf812a399b17ad0c114a1ece7986b66a4d83a4b92ab58d`. Live https://play.62-146-235-120.sslip.io/ does **not yet contain this Player fix**. Next milestone is Claude combined perf/audio+attachment Player package, Singapore client rollout and live verification. Existing scope authorizes it with rollback; New York excluded. Release DoD remains open until live verification; local checks are not live acceptance.

Other observation for Claude: Unity runner scene teardown logs existing `PlayerAssignmentDialog.OnDisable` line43 NullReferenceException after a successful test report; no attachment test errors. Native resource hand scales intentionally retain §54.12 authoring (including large palm crowns); all10 tools use ObjectFit targets exactly. No geometry/catalog entries altered.

## Sveta coordinator: release gate for backpack correction
Received Codex report and independently read CURRENT OVERRIDE in both shared documents. Claude: before publishing the combined player, acknowledge d42be421673599ceb719728fa6dd357d6802f8a7, sync the corrected files ONLY after the current Unity build finishes, rerun the affected tests and rebuild. Do not publish the superseded backpack-clearance behavior. Preserve metre anchor and other fixes. Please record ACK and final source/execution revision in this document; file delivery is not proof of consumption. No request to interrupt Unity or touch production from this message.

## Codex — wardrobe + requested backpack correction VERIFIED, Unity released (2026-10-09 22:37Z)
Production fixes: **59ee2c4e9979314be1f59e71de136008a5ef8ac7** (wardrobe), **d42be421673599ceb719728fa6dd357d6802f8a7** (preserve backpack/tool overlap). Unity CLI graphics PlayMode **3/3 wardrobe + 5/5 attachment** PASSED, both CLI exit0; final test interval22:34:31–22:36:10Z. Exact public WebGL wardrobe payload loaded/unloaded twice, six yaws and both real footwear models checked, including translated/scaled parent and repeated shelf sync. Source gate3/3 and offline binary audit passed. Before/after renders reviewed: hangers vertical; boots on board. Corrected attachment render reviewed: pack overlap remains. Old attachment-comparison montage remains obsolete; individually refreshed attachment-*.png files show the corrected behavior.

Reports (sanitized; command-line/environment removed): `Assets/HexLiveContent/People/Validation/wardrobe-bundle-playmode.xml`, `prop-attachment-playmode.xml`; review `Review/wardrobe-before.png`, `wardrobe-after.png`. Existing PlayerAssignmentDialog.OnDisable teardown NRE occurs AFTER test report, separately handed to Claude; not a test failure and not concealed.

Five production files copied/compiled and SHA256-equal source/execution: Environment/{WardrobeAssembly,WardrobeHangerFactory,WardrobeHangers}.cs + Wearing/{NpcActorView,BodyBones}.cs. UNITY_OWNER removed only after both CLI processes exited. Codex has STOPPED execution writes: Claude may freeze/build v6 with current shared HEAD. No content/server/world changes required; exact wardrobe payload SHA `dc7afbd3d5c873fe68107c164b07a9f0727651eb2fc57aa680a37b5188288f01`, existing content package SHA `82e2605f3335ffbd2eaf812a399b17ad0c114a1ece7986b66a4d83a4b92ab58d` unchanged. Live https://play.62-146-235-120.sslip.io/ still needs new Player; local regression is not live acceptance. Next milestone: Claude v6 package/deploy/live receipt; New York untouched.

## Claude — client v6 published: performance (2026-10-10 ~06:15 +07)
- Client `webgl-site-4c60d5a4abe6d5088d1124f8ccd62c8364495705.tar.gz`, **SHA-256 `94555127f11df4b755f98369f5df027ae9f60d3169027244741d5693df64abf8`**, 157432806 bytes. Includes Codex d42be4216 (backpack overlap restored,
  NOT the rejected clearance) + 59ee2c4e9 (wardrobe hangers/boots), and perf 2a5cfaa0d/40bb336af/750962bb1.
  Unity 15.1 min, 0 errors; v5 (LTO, contained rejected clearance) was stopped and never published.
  `current` → `releases/4c60d5a4abe6d5088d1124f8ccd62c8364495705`; previous `9e1a9215c…-dpr1` (v4 + live DPR patch) kept. Server/world/content unchanged.
- Build facts: code optimization RuntimeSpeed (was DiskSizeLTO / -Oz); WebAssembly 2023 on; exceptions FullWithoutStacktrace
  → JS `invoke_*` wrappers 509 → **0**; wasm.br 17.5 → 13.8 MB; devicePixelRatio 1 baked into index.html; FMOD 4×2048.
- **Measured live, same Apple M1 / Claude app pane, visible:** in world 20 s — **59.9 fps**, rAF median 17 ms / p95 21 ms,
  long tasks (>50 ms) **0** (v4: median 105 ms, p90 196 ms per frame of main-thread CPU). Audio: running, 430 windows,
  0 silent. Console errors/AtomicContent/ERR_: 0. Load: server connected 0.5 s, curtain ~15 s after Continue (warm cache).
- Observation for Codex (not investigated, did not touch the live session): Tessa rendered without clothes on day 3 at 35 °C;
  loader said bodies ready, inventory clothes tab empty — likely sim-side undress, please confirm. Roster shows initials.
- Screenshot: `/Users/shtolyan/hex-girls/webgl-build/release-evidence/20261010-singapore/live-world-v6-claude.jpg`.
- Player command still unverified (guest session).

## MSI release 20261011-1704 — current limited authorization

Tolya authorizes the existing MSI worker to integrate the five visual-fix commits,
build WebGL content and publish that validated content on Singapore 62.146.235.120.
MSI owns the bundle release; Claude/Mac matching-player ownership and the single
production writer handoff must be acknowledged before activation. No content is
activated against an incompatible player. Existing worlds/saves and rollback are
preserved; no server binary/world replacement, New York, Flashback or Coil change
is included in this order. The older world-replacement scope above is not reused.

MSI fetched origin/master = 820f7ca7b674dc6e5c8d2df9293f4258c8e3084a, already an
ancestor of codex/msi-visual-bugs / 042b97409e98be1af8db3bb57d39b84b63327c31.
All five named visual-fix commits are present. Unpushed Mac work is not assumed
integrated. PeopleAssetPreparation.cs overlap requires Mac review. Unity6000.4.5f1
WebGLSupport is installed; Windows content adapter reuses canonical inventory,
serialized payload and package validation. Build input receipt records dirty
tracked hashes. Matching player must include SkinBandageAtlas/Resources and
SkinTexturePainter changes; live v11 compatibility is unproven.

Next: local bundle build/validation; obtain exact current client source/build
receipt and Mac publisher ACK through Sveta; staged rollout with rollback, public
hash verification and live visual/performance QA. The denied 29MB evidence archive
is still excluded. Heartbeat stays disabled. No new worker/worktree/checkout.

### MSI bundle result — 2026-10-11, source dabccb70

Unity 6000.4.5f1 completed the WebGL content build at 10:55:27 UTC with exit 0:
229/229 bundles, no failures. The canonical validator reopened 121 People objects,
held five bundles together and checked 23 renderer/paint-map contracts. Including
raw simdata, the validated package has 230 objects/blobs and 306,968,282 payload
bytes. Browser rendering is explicitly not yet validated.

- Source commit: `dabccb70c7ad5bf480e2e40fc633a46be114f6b1`; the build receipt also
  records 189 preserved dirty tracked inputs. Unity additionally registered
  `SENTIS_ANALYTICS_ENABLED;MAGICACLOTH2` for WebGL in ProjectSettings; prior
  version changes remain intact. Other pre-existing dirty hashes were unchanged.
  Comparing the complete tracked path set after the build also found newly dirty
  `Assets/Settings/Mobile_RPAsset.asset`: only its `m_Prefilter*` shader-variant
  fields changed. This is preserved separately from the 189 pre-existing dirty
  inputs; the checkout now has 190 dirty tracked paths. Neither settings file
  was included in the report/test commit or reset.
- Package manifest SHA256:
  `38a87018e11abaad905a0388edcecc7aae4daf985fd5727628922c48c634583d`.
- Game package archive: 277,733,863 bytes, SHA256
  `635f69561941865aa4b6846755f349ffc860a58e3f3c55f6856026bb041fae69`.
- Local plan has 215 changed objects. Keep identical blobs and the current
  simdata revision: old/new simdata JSON is semantically identical. Object IDs,
  metadata and states match the 230-object live baseline.
- Hair transfer size increased from 7,515,069 to 46,986,612 bytes. This is not a
  GPU memory or frame-time measurement; multiple-actor WebGL QA remains required.
- Singapore staging:
  `/var/lib/hexlive/assets-webgl/staging/msi-release-20261011-1704-dabccb70`.
  Rollback candidates preserve all 230 old records and verified payloads. Roll
  back through the installed publisher with new revisions, never by restoring
  an old registry cursor. Upload exited 0; server verification at 11:08:12 UTC
  matched the archive/verifier/manifest hashes and all 230 payloads. The prepared
  `activation-candidates/` contains only the 215 planned changes and does not
  publish them. Revalidate current registry and payloads before using it.
- No activation: live registry remains 230 and the current Player is v11, whose
  source SHA is unverified. Sveta confirms Claude has not acknowledged a matching
  Player or single-publisher handoff; MSI Player build ownership is not agreed.
  Matching Player needs SkinTexturePainter/SkinBandageAtlas plus two atlas assets
  and 24 textures under Resources/HexLive/Decals/BodyAtlases.

Eight focused package tests pass, including same-size blob corruption rejection.
The canonical hash validator required no code change. Local receipts and the
activation plan are under `Build/MSIRelease/20261011-1704/`. Worlds, saves, server
binary, New York, Flashback and Coil are not release mutation targets.
# Activation receipt — 2026-10-11 14:21 UTC

Tolya directly ordered immediate activation of the prepared package while the
new Player was building. MSI activated Singapore content using the installed
publisher as `hexlive`: 215 objects, exit 0, registry **230 → 445**. All 230
public index entries were compared with the release plan: zero hash mismatches.
Simdata, worlds/saves and server binary were unchanged; no service restarted.

Package source remains `dabccb70c7ad5bf480e2e40fc633a46be114f6b1`, with the
previously recorded dirty inputs. Manifest SHA256:
`38a87018e11abaad905a0388edcecc7aae4daf985fd5727628922c48c634583d`.
Remote receipt and rollback remain under
`/var/lib/hexlive/assets-webgl/staging/msi-release-20261011-1704-dabccb70/`.
Player at activation still resolved to directory
`4b34c09a6a3f913b29f602b77e48fc0003419cff`; this is not a verified source SHA.
The matching new Player and live visual QA are still required. Sveta notified.

New Flashback #435 work (position-map precision, wound sizing and dirt angle
fade) is **not** in this package. It is undergoing native Unity validation.


## SVETA-RELEASE-OWNER-20261012-02
Coordinator allocation under existing Tolya release order: MSI is sole Singapore CONTENT publisher; Mac builds/publishes matching Player only. Mac must not activate assets-webgl concurrently. Request existing Claude ACK and finished build/deploy receipt (source SHA, dirty inputs, artifact/public hashes, wound8d2cae8e and bandage7c5ea3d9 plus BodyAtlases). This entry is NOT an ACK or compatibility acceptance. Unity30397 observed compressing player-434-measure WASM; last Claude text17:39Z awaiting build. Do not interrupt build. Prior --ask failure is not delivery proof; no new Claude instance.

## Claude — Singapore: new server + client, BigIsland world, accounts + voice (2026-10-12)
Direct order from Tolya (12.10): finish #434, build from master, publish to Singapore, then start a new
BigIsland world under the new (§171) rules. New York, Flashback, Coil and keys untouched.
- Source: master `7cd230bda39202a3a5a84da98106109bf270aae1` (protocol 24; old server `88ee8821b` was 23).
- Tests in a clean worktree of that SHA (Release): Simulation 1700/1700 (+6 skipped), VoiceBrain 5/5, Server 466/466
  in the dedicated run. `QueryAllocationDependsOnOutputLimitRatherThanRememberedCount` flakes ONLY under parallel CPU
  load (549512 vs ≤547200 bytes); separate task to stabilise it.
- Server archive SHA-256 `642c0c09ccd1dfbe79138e14f5a601e16aa93ea948adf6586621ab743e6d4bea` →
  `/opt/hexlive/releases/7cd230bda…`; previous `88ee8821b…` kept.
- Client (release WebGL, FSB audio 2872, no BurstDebugInformation) archive SHA-256
  `2352b37b6a382d7bf382342d12d52580266d78fee99c02a590aa5c9c273340e8` → `/var/lib/hexlive-web/releases/7cd230bda…`;
  previous `4b34c09a6…` kept. Content registry untouched (447 revision, 230 active WebGL).
- World: previous feud world saved at tick 682829 and MOVED (not deleted) with simdata, drop-ins, Caddyfile,
  previous symlink targets and secret hashes to `/var/lib/hexlive-archive/20261012-pre-bigisland`. New world:
  `--mode bigisland --seed 1288076543`. Admin/player/MCP secret hashes unchanged.
- Accounts: `--firebase-project hexlive-game`, providers google,password (Apple pending Service ID).
  Voice: `/etc/hexlive/voice.env` (0600 hexlive), separate ElevenLabs agent `agent_2301…` whose brain URL is
  `https://play.62-146-235-120.sslip.io/api/voice/v1/brain`; state/usage in `/var/lib/hexlive/voicebrain`.
  Drop-in `zzzz-bigisland-accounts.conf`.
- Caddy (play host): `/login` → game server; `/play/` → `/` (302). Repo `Server/Caddy/singapore-webgl.caddy` = prod.
- External: `/` 200, `/login/` 200, client-config 200, wasm 200, asset index 200, `/watch` 101, flashback/keys/admin 200.
- Live browser (guest, Claude app): connect 3.1 s, curtain ~125 s on a cold cache, day 1, Jolly/Wren dressed, live
  portrait; exceptions 0, console errors 0, missing-record errors 0. FPS not measured (pane throttled).
- NOT verified: real sign-in and a voice call on prod (agents may not create real Firebase accounts) — Tolya.

## MSI — Singapore server + Player, 2026-10-13 12:28 UTC

Tolya directly ordered two-decimal balance display, commit/push, branch refresh,
build and deploy to Singapore. MSI published both runtime layers; content was
preserved. Source `f7a20d6cd3d21745a42e6d7759702eeccb842d52`, protocol24.
`codex/msi-visual-bugs` was pushed and pulled with `--ff-only` before the build.
The release includes #443 voice setup/error handling, #444 precise usage ledger,
automatic final voice reconciliation and USD display with two decimal places,
#452 browser API corrections and #453 standing/movement synchronization.

- Server archive SHA256 `cec9ee7980671a95a5ccb300c93260981c1a0e9d31333dd80bcaade9d2e52368`;
  521 staged files verified. `/opt/hexlive/current` changed from
  `/opt/hexlive/releases/dae0138061c9521489ada91c14d4f8bee82bd5b3` to
  `/opt/hexlive/releases/f7a20d6cd3d21745a42e6d7759702eeccb842d52`.
- Player archive SHA256 `23b58bb1967fac1e71aae1e9130781f6642a870415717a04abc3f7f86dd9f850`;
  19 staged files, 18 final public files downloaded and hashed.
  `/var/lib/hexlive-web/current` changed from
  `/var/lib/hexlive-web/releases/msi-20261013-dae013806` to
  `/var/lib/hexlive-web/releases/msi-20261013-f7a20d6cd`.
- Canonical Unity6000.4.5f1 CLI builder succeeded: 234MB, 0 errors, 562 warnings,
  18.4 build minutes. Log confirms RuntimeSpeed, FMOD4×2048/WebAudioTuning,
  audio manifest7984, success and devicePixelRatio1. Dirty project settings
  restored byte-for-byte; foreign material/editor changes preserved.
- Verified reuse of audio source `7cd230bda39202a3a5a84da98106109bf270aae1`:
  all7984 source files unchanged, all5668 copied FSB/vis hashes checked,
  FSB5 signatures checked, reuse marker removed only after verification.
  MSI reuse skips WriteWebMusic; the narrow documentation correction is
  `7a5ff754011ecd6f0597b71b4422df1b224574af`, not new Player runtime code.
- Checks: native UI18/18, Server485/485, Voice14/14, JS12/12, tooling7/7;
  strict WebGL compiler0 errors/0 forbidden APIs. Full Simulation1749 passed
  with6 existing skips at the unchanged simulation source from #453.
- One `hexlive.service` restart; Caddy/configuration unchanged. Stopped-world
  backup, accounts, keys and voice usage backup retained privately under
  `/var/lib/hexlive/ops/msi-balance-release-20261013-f7a20d6cd/activation-backup`.
  No world reset, no credit regrant, assignments preserved; tick392461 loaded
  and continued392467. Registry/catalog604 and all230 content records unchanged.
  Previous server/Player remain for rollback. New York was not modified.
- Public verification passed: Brotli/WASM MIME, complete Player SHA map,
  voice client.js matches source, audio sidecar hash and anonymous live
  wire24 snapshot in the same running world. No player orders issued.
- In-app Chromium opened loader `player-f7a20d6cd.loader.js` and rendered the
  centered server menu. FMOD log: AUDIOWORKLET,48000Hz,2048×4. Singapore ping
  was positive456ms (the separator is `·`). Two startup NullReferenceExceptions
  without stacks and missing NY `closedtest.unavailable` localization were
  recorded on #439; matching the earlier reported NRE cause is unproven.
  Real authenticated balance/join/call and standing visual QA were not performed
  in this browser; native checks do not replace the player's live confirmation.

Operational receipts/logs are local at `Build/MSIRelease/20261013-balance-release/`;
the public runtime receipt is `/release-receipt.json`. No source/evidence archive
was exported to Flashback. Transfer recovered from a verified-complete stalled
SFTP by stopping only MSI's own copy process and using standard SCP with strict
host checking; production pointers remained unchanged until guarded activation.

## MSI — Singapore server + WebGL Player, 2026-10-14 05:05 UTC

Tolya explicitly authorized both runtime layers and separately approved committing
the previously prepared MSI Unity materials/import metadata/settings. Source
`05ba952da9370c79c7e83c56a4c0a45a1d5ebbe6` is pushed in `codex/msi-visual-bugs`.
The release includes #463 autonomous neutral-camp property admission, plus the
previously committed UI, voice, inventory and wardrobe changes on this branch.
Content/model bundles and New York were preserved.

- Server archive SHA256 `33d6c2303158fc062576a0179e53d0266c0b4ec3ffaa5215e895ef3f54f3bf7d`;
  521 files verified. Server pointer changed from
  `/opt/hexlive/releases/f7a20d6cd3d21745a42e6d7759702eeccb842d52` to
  `/opt/hexlive/releases/05ba952da9370c79c7e83c56a4c0a45a1d5ebbe6`.
- Player archive SHA256 `6e46d5a5fbe4ec075d0014a039eab27ead8e99b08f5d29c2481b86d8634d9310`;
  19 staged files and 18 final public files hashed. Player pointer changed from
  `/var/lib/hexlive-web/releases/msi-20261013-f7a20d6cd` to
  `/var/lib/hexlive-web/releases/msi-20261014-05ba952da`.
- Canonical `HexLiveWebGLPlayerBuild.Build`, Unity6000.4.5f1 CLI: 234MB,
  0 errors, 563 warnings, 19.5 build minutes. Verified RuntimeSpeed,
  FMOD4×2048/WebAudioTuning, audio manifest, success and devicePixelRatio1.
  Auto Refresh and original project settings restored; runtime inputs unchanged.
- Audio source `7cd230bda39202a3a5a84da98106109bf270aae1`: 7984 source
  hashes verified unchanged; 5668 staged FSB/vis files hashed, FSB5 signatures
  checked. Reuse marker removed only after verification.
- Full Server495/495 and Simulation1767 passed, 6 existing skips;
  focused simulation185 passed, MCP clothing21 passed, strict WebGL0 errors/0 RS0030.
  Simulation was rerun completely after Unity using VSTest with process-only
  GCConserveMemory9 to avoid competing memory pressure; no test was suppressed.
- One `hexlive.service` stop/start, Caddy/configuration unchanged. Private
  stopped-world/accounts/keys/voice backup remains under
  `/var/lib/hexlive/ops/msi-camp-release-20261014-05ba952da/activation-backup`.
  World identity/seed/topology, accounts, keys and assignments preserved.
  Saved tick631722 loaded at631732; anonymous live snapshot632307 on wire24,
  paused=false, speed1. Later health tick633530, 2/2 colonists, rate3.94 ticks/s.
  Previous releases retained for rollback; no world reset or credit regrant.
- Public verification: all18 Player hashes, Brotli/WASM MIME, source-exact
  voice client.js, audio sidecar, registry604 and all230 content records unchanged.
- Live in-app Chromium rendered the central server menu with
  `player-05ba952da.loader.js`; FMOD AUDIOWORKLET48000Hz/2048×4.
  Singapore ping431ms, NY disabled. Two startup NREs without stacks and
  untranslated `closedtest.unavailable` observed again and recorded on #439;
  identity of their cause is not established. Screenshot saved locally.
  Real authenticated join/call and live foreign-camp actions were not exercised;
  no player orders were issued. #463 remains ready_for_test for the player's QA.

Operational receipts are local at `Build/MSIRelease/20261014-camp-release/`;
public receipt is `/release-receipt.json`. No evidence/source archive exported
to Flashback. The TrailPack geometry fix #462 still needs a separately authorized
content/bundle release; this runtime publication did not activate those models.

## MSI — Singapore release staged, 2026-10-14 11:26 UTC

Tolya requested a clean commit/push, a WebGL build and Singapore client
publication. Build source `f2576372ca5a26a279936cdc68695a78ed557d24` is pushed
in `codex/msi-visual-bugs`, including #454/#464–470 and the separate local
audit/build-output ignore change. Local diagnostic models and reports remain
preserved. No new worker/worktree, Unity MCP, heartbeat or New York change.

- Canonical `HexLiveWebGLPlayerBuild.Build`, Unity6000.4.5f1 CLI:234MB,
  0 errors,567 warnings,20.3 build minutes, editor exit0. Mandatory RuntimeSpeed,
  FMOD4×2048/WebAudioTuning, manifest and devicePixelRatio1 lines verified.
  Auto Refresh and original settings restored; runtime inputs unchanged;
  working tree clean before build and again after its temporary overrides.
- Full Server496/496 and Simulation1811 passed,6 existing skips; focused leaf
  checks85 and native leaf material/GPU checks5 passed; strict WebGL0/0.
  Full simulation used process-only GCConserveMemory9; no tests suppressed.
- Audio source `7cd230bda39202a3a5a84da98106109bf270aae1`:7984 original source
  files verified unchanged. Singapore staged5668 FSB/vis files, all hashes
  checked and FSB5 headers verified; reuse marker removed only after success.
- Player archive SHA256
  `ba3c32671602ab794c8c4bd75f42db02bce45d2faeb905d57cd59566cbc96ffc`;
  19 staged files verified under
  `/var/lib/hexlive-web/releases/msi-20261014-f2576372c`.
  Stage receipt has `activated:false`; public pointer remains
  `/var/lib/hexlive-web/releases/msi-20261014-05ba952da`.
- Matching linux-x64 server package521 files prepared **locally only**;
  SHA256 `ca1ce32b0ebc482a40b0247e705f8e3a4d52219d60656a5639d5ec8d36978f94`.
  Installed server remains `/opt/hexlive/releases/05ba952da9370c79c7e83c56a4c0a45a1d5ebbe6`.
- **Activation gate:** installed Player/server use wire24; new Player/server
  use wire25 and save84 (reads66..83). A client-only pointer switch would break
  connections. Exact approval to update the Singapore game-server version,
  preserve world/accounts/assignments and restart hexlive.service once was
  requested from Tolya and remains pending. Neither pointer switched;
  no service restart, world reset, account change or content activation.
- Anonymous baseline handshake: existing world
  `a309de74171d470385412bfab01ffdab`, seed629209070, HugeIsland mode2,
  topology59DDDCBB, speed1, paused=false, wire24; no assignments/orders.
  Registry604 and230 content records retained. Public new-Player browser QA
  and authenticated gameplay/call QA await compatible activation.
- Rollback preparation must restore the stopped-world backup before restarting
  the old executable after a failed activation: it cannot read a new save84.

Operational receipts are local at
`Build/MSIRelease/20261014-flower-leaf-release/` and staged under the private
Singapore ops directory `msi-flower-leaf-release-20261014-f2576372c`.
This entry is a **staging receipt, not a publication receipt**.

## MSI — Singapore published, 2026-10-14 11:51 UTC

Tolya explicitly approved both the Singapore client and game-server update
from `05ba952da` / wire24 to `f2576372ca5a26a279936cdc68695a78ed557d24` / wire25, preserving
world, accounts and assignments with one `hexlive.service` restart. This
publication receipt supersedes the pending activation state above.

- Server: `/opt/hexlive/releases/f2576372ca5a26a279936cdc68695a78ed557d24`; all 521 files
  hash-verified and executable checked on Linux before activation.
- Client: `/var/lib/hexlive-web/releases/msi-20261014-f2576372c`;
  paired pointer activation completed at `2026-10-14T11:51:09.596196+00:00`.
- Stopped-world backup retained in the private ops directory
  `msi-flower-leaf-release-20261014-f2576372c/activation-backup`.
  Previous server/client releases retained; rollback must restore that
  stopped-world save before starting the old save-format reader.
- World identity/seed/topology unchanged; saved tick 729033 loaded
  at 729037, anonymous wire25 snapshot 729102.
  Accounts/keys hashes and players.json assignments matched at activation;
  no world reset, credit regrant, content activation or New York changes.
- Public checks passed: all 18 build hashes, Brotli/WASM MIME, source-exact
  voice client.js and sampled audio sidecar. Registry 604 and all 230 content
  records retained. Full Server 496 and Simulation 1811 passed, 6 existing
  skips; strict WebGL 0/0, focused leaf 85 and native leaf material 5 passed.
- In-app Chromium loaded the new Player and rendered the central menu.
  Singapore ping 441ms; FMOD AUDIOWORKLET 48000Hz, buffer 2048x4. Two previously
  observed startup NREs without stacks and untranslated New York
  `closedtest.unavailable` recur; tracked on #439, not claimed fixed.
  No authenticated join/call, live Nika actions or drying-time gameplay
  test was performed; reports remain ready_for_test for player validation.
- Follow-up service health: tick 729467, 2/2 colonists, 3.86 ticks/s,
  average 8.20ms/tick. No additional restart.

Runtime/source receipt remains `f2576372ca5a26a279936cdc68695a78ed557d24`; later documentation
commits do not change that build identity. Local operational receipts are
under `Build/MSIRelease/20261014-flower-leaf-release/`; public receipt is
`https://play.62-146-235-120.sslip.io/release-receipt.json`.
No source/evidence archive exported to Flashback.

## MSI — menu startup NRE diagnosis and packaging fix (#471), 2026-10-14

Direct Tolya task: fix the two menu startup NRE, with no new Player build or
deployment. Both public Studio-bank URLs returned HTTP404 with zero bytes:
`StreamingAssets/FMODBanks/Master.bank` and `Master.strings.bank`. Both files
are present in the existing f2576372c Unity output but absent from its runtime
archive. The old private packaging filter excluded all StreamingAssets.

Local Unity reproduction exercised the installed FMOD web-loading coroutine:
empty HTTP404 yields null data, then `RuntimeManager.loadFromWeb` line925
calls `FMOD.Studio.System.loadBankMemory` line676, which dereferences
`buffer.Length`. Both missing-bank cases reproduced those exact NRE stacks.
The same two committed artifact banks over local HTTP200 completed without
NRE (the Editor's already-loaded banks produce the expected SDK warning).
Native comparison tests2/2 passed; no exception suppression was added.

`Tools/webgl_package_player.py` is now the documented Player archive path:
reuse skips only StreamingAssets/HexLive, preserves Studio banks and other
runtime files, verifies RIFF/FEV sizes and exact committed/LFS bank hashes,
then archive membership and hashes. The historical private wrapper routes to
this helper; it was not executed again. Docs/WebGLBuild.md and Spec/168.md
require public HTTP200/hash checks for both banks. Tool tests16/16 passed,
including nine packaging regressions. Existing artifact layout passes with21
files; its historical19-file archive is correctly rejected for missing banks.

Verified source f2576372c bank SHA256:
- Master.bank: `5441eae1abd29377a36d2e54c5dde0f3573d6d55bb5d4ecca7c7336ede6a3b16`
- Master.strings.bank: `757ca5591fe4a91b635f21d4d6f74bf945d36b3acdbd171f0a77f7ce88abffae`

No Unity Player build, runtime source/SDK edits, package reactivation, service
restart or production changes. The two bank files already exist; a later
explicitly authorized site-file update can use that verified artifact without
recompiling Unity. Public absence/NRE remains until that update and live QA.
Local diagnostics (including temporary Unity test source, exact stacks and
receipts) are preserved at `Build/MSIRelease/20261014-menu-nre/`; not exported.

### #471 — two Studio banks published under new exact authorization

Tolya subsequently authorized publishing only Master.bank and
Master.strings.bank to Singapore, without Unity rebuild or game-server update.
At server UTC `2026-10-14T12:27:37.000361+00:00`, the two source-verified files
were atomically added as `StreamingAssets/FMODBanks` in the existing
`msi-20261014-f2576372c` client release. No previous client file was replaced.
All 5688 pre-existing file hashes remained identical, including the public
release receipt; the supplemental bank-patch receipt is in private ops
`msi-fmod-banks-20261014-f2576372c/bank-patch-receipt.json`.

- Public HTTP200 and SHA checks passed for both banks, matching the hashes
  recorded above (Master.bank 21521248 bytes; strings 12686 bytes).
- Existing Player/server pointers, service PID/start/restart count, registry
  state and active-world identity remained unchanged. Zero service restarts,
  no server deployment, Unity compilation or new Player build.
- Fresh anonymous in-app Chromium startup rendered the server-selection menu
  (Singapore ping176ms). Both FMODBanks fetches are present in the observed
  resource inventory; Console error list is empty and NRE count is zero.
  FMOD reports AUDIOWORKLET48000Hz/2048x4. Existing URP warnings and the
  New York untranslated label remain outside this fix. No authenticated
  join/call was tested. Player confirmation still owns #471 -> fixed.

Rollback is limited to removing the newly added two-file FMODBanks directory;
no existing files/pointers were overwritten. Bank-patch, public hash and
browser receipts are local under `Build/MSIRelease/20261014-menu-nre/`.

## MSI — character creator client published (#473), 2026-10-15

Tolya authorized checking real character creation and publishing the Singapore
client. The published Player source is
`d23e9cddb8dfc7e7e7857819f27a3874c8d1249e`; later documentation commits do not
change that build identity. This release updates the client only.

- Official Unity6000.4.6f1 Editor and matching WebGL installers were repaired
  through the U646 junction to the same existing installation. Both exited0.
  Only35 missing package-cache files were restored from the repaired official
  installation; no existing cached file was overwritten or deleted.
- Creator PlayMode tests26/26 passed on6000.4.6, including pointer-click and
  Cyrillic typing. Strict WebGL compilation passed with0 errors and0 RS0030.
  Audio-source reuse proof passed for7984 sources against the actual published
  audio source `7cd230bda39202a3a5a84da98106109bf270aae1`.
- Canonical `HexLiveWebGLPlayerBuild.Build` exited0:234MB,0 errors,
  570 warnings. AutoRefresh preferences and original ProjectSettings/QualitySettings
  bytes were restored. The release started from a clean tracked tree.
- Canonical Player package SHA256:
  `6dc5393a5f56a7f5d5fe80a06b1dd2b7c58a062318319e4364a63b7e6725b009`.
  All21 packaged files, both source-exact Studio banks, and5668 reused published
  audio files were hash-verified at staging.
- Browser QA clicked the name field, entered the complete `Кшиштоф`, retained
  the name through gender/step changes and used Continue. Exactly one dedicated
  QA character, `Кшиштоф QA473-1015`, was created through the interface and
  entered the island. Server assignments independently confirmed NPC1000000002
  in world `a309de74171d470385412bfab01ffdab`.
- At server UTC `2026-10-15T06:07:01.118384+00:00`, the client pointer changed
  from `/var/lib/hexlive-web/releases/msi-20261014-f2576372c` to
  `/var/lib/hexlive-web/releases/msi-20261015-d23e9cddb`. The old release remains
  available for rollback. Server binary `f2576372c`, protocol25,
  `hexlive.service` PID1925094, world identity and registry604 were retained;
  zero service restarts. New York was not changed.
- Public checks passed for all20 published build-file hashes, including both
  FMOD banks, Brotli/WASM headers, sampled FSB/vis, and all230 unchanged content
  records. A fresh join from the main public URL entered the same QA character
  without opening creation or creating a duplicate. Browser NRE0/errors0.
  Temporary desktop viewport override was restored afterwards.

QA NPC1000000002 remains assigned to the dedicated test account. Standard
destructive admin commands require the player's confirmation in the viewer
(`AdminCommandBus.Confirm`); no confirmation bypass or direct save edit was
attempted. The separate #474 bandage-priority server fix is committed in the
Player's source history but has **not** been deployed to the authoritative
game server by this client-only release.

Operational receipts and screenshots remain local under
`Build/MSIRelease/20261015-creator-release/`. Public source receipt:
`https://play.62-146-235-120.sslip.io/release-receipt.json`.
No source/evidence archive was exported to Flashback. #473 remains
ready_for_test; only the player confirms fixed.
