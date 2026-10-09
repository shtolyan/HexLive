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
