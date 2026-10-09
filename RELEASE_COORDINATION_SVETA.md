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
