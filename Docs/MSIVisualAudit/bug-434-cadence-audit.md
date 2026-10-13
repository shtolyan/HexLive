# Bug 434: portrait cadence audit, measurements still required

Live observation on 2026-10-11: see `bug-434-live-20261011.json` for all 139 raw samples.
At https://play.62-146-235-120.sslip.io/ the observed scripts were
`Build/WebGL-v11.loader.js` and `Build/WebGL-v11.framework.js.br`. Deployed Git SHA
is still unknown; local/Mac HEAD is not a substitute. Browser: Codex In-app Browser;
Unity log renderer: `WebKit WebGL` (not a verified GPU model/browser version).

Nadia was selected automatically; current loading logs identify her as NPC 1.
The UI showed Busy and warming by the fire, then drying clothes after the sample.
She was visibly awake and animated. No player commands were sent. Sampling ran
2026-10-11 07:42:48.900–07:43:08.990 UTC in a visible foreground tab, viewport
1100x750, canvas 960x600, fixed face crop x84 y543 w90 h100. Sequential screenshot
capture produced 139 JPEGs, 111 adjacent encoded-image hash changes and 27 equal
pairs. First-to-last completion span was 19.900 s: 5.58 observed changes/s.
Median capture interval was 142 ms, p95 172 ms. Those are capture intervals,
not camera or visual-update intervals. Animation/lighting changed during the run.

This is sampled visual evidence, not exact RenderTexture updates or camera FPS.
JPEG comparison can miss small changes; sampling can miss multiple updates.
Screenshot capture itself adds browser overhead. No rAF count or CPU/GPU render
cost was measured. This result is compatible with roughly six-Hz visible cadence,
but does not establish the deployed limiter or rule out intermittent stalls.
Exact build receipt and existing Mac profiler camera timings remain required.
No runtime files changed, no diagnostic rebuild or publication performed.

Inspected local source at `a4cf3766bdb116f4d212676829fcc8c4a2c8e711`.
No changes to the Mac-owned `PortraitStage.cs` or `CharacterPanel.cs`.

`PortraitStage.LateUpdate` limits camera activation only under
`UNITY_WEBGL && !UNITY_EDITOR`. It disables the camera until unscaled time
reaches the next deadline, then sets a new deadline to the current time plus
`1f / 6f`. This is a maximum cadence, quantized to available player frames;
it is not a guarantee of exactly six rendered frames each second.

The deadline advances before resolving the world renderer/actor face. Missing
actor data can therefore skip a scheduled render. `SetTarget` does not reset
the WebGL deadline, so a newly selected actor can wait for the next interval.
These are code observations, not reproduced causes of the player's complaint.

`CharacterPanel.BindPortrait` binds `PortraitStage.Texture` directly as the
UI Toolkit background. This path has no separate portrait snapshot cache or
independent update timer. That does not by itself prove the browser redraws it
at the expected cadence.

The Editor excludes this WebGL limiter. An Editor screenshot or PlayMode test
cannot establish the actual browser camera frequency or render cost.

Requested from Sveta: shared live URL, deployed client commit and a reproducible
selected NPC. No new deployment requested or performed. Required measurement:

- Keep the browser foreground for 20 seconds, with a moving/animated selected
  actor and a stable UI layout. Record player frame intervals and portrait
  visual change intervals separately; repeat with the portrait hidden.
- In the corresponding Mac diagnostic build, record camera begin/end events
  for `PortraitCamera`, the target identity and whether actor/face resolution
  succeeded. Count actual camera renders rather than `LateUpdate` calls.
- Correlate RenderTexture content changes and visible UI changes with camera
  events. Static actor imagery alone cannot prove that the camera did not render.
- Report median/p95 interval, total renders, player FPS, and render timing.
  CPU time between pipeline callbacks is not GPU time; label it accordingly.
- Compare normal selection, target switching and hidden portrait cases. Missing
  data, browser throttling and a slow overall frame rate must be distinguished
  from the intentional six-Hz cap.

Current result: the source explains an intentional low cadence, but neither an
additional runtime defect nor a final by-design outcome is established. #434
remains in progress until shared-build evidence supports that decision.
