# Secondary bone motion prototype — MSI, 2026-10-11

This is an opt-in local prototype, not a production attachment or a release.
`SecondaryBoneMotion` contains no MagicaCloth implementation. Its constrained
Verlet approach is adapted from UnityChanSpringBone under the adjacent MIT notice.

## Reproduction

Use the existing checkout and the native Unity 6000.4.5f1 editor, exclusively.
Launch with `-batchmode -projectPath <checkout> -executeMethod
MsiSecondaryMotionDemo.Begin -logFile <log>`. Do not add `-nographics` or `-quit`:
the fixture enters Play Mode, captures camera renders and exits itself.
Coordinate the Editor owner before launch. No Unity MCP is involved.

The fixture creates two Marta instances with the normal BodyBones/Wear equip path
and PrimalTop/PrimalBriefs. Left has no secondary motion; right has four authored
Leony ponytail joints and two pectoral joints. Both use the same real humanoid
Running, Jump and Standing Idle clips and the same scripted root trajectory.
This is an isolated animation test, not AI navigation or a live server session.

Capture: 420 rendered frames at a simulated 30 Hz, 1280x900. Run 0–4 s,
jump 4–6 s, run and turn to rear 6–11 s, stop and settle 11–14 s.
The last two seconds hold the animation pose to expose residual solver motion.
Frame production is offline and is NOT a measurement of real-time game FPS or CPU.
Output: `Build/MSIVisualAudit/SecondaryMotion/frames`, `measurements.csv`,
`result.txt`, `unity.log`. The local encoder adds labels; it does not synthesize
character frames. The first 22-degree pass was rejected for excessive tail curl.
Generic solver defaults: stiffness 240, damping 24, max angle 14 degrees per joint,
root translation inheritance 0.8. Breast tuning: 420, 22, 5 degrees, 0.85.

## Integration boundaries

- No production prefabs, hair exports or shared runtime callers are changed.
- Prepared lightweight hair lost its strand rig/weights during export. The demo
  therefore uses the original Leony rig with current source-UV cutout materials;
  preservation/transfer of those original weights is still required for lightweight
  production hair. Do not pretend adding the solver restores missing weights.
- The body/clothes use the current prepared Marta assets. Breast bones rotate;
  this does not enlarge the body or change simulation anatomy.
- The demo removes old cloth components only from disposable scene instances.
- Collision uses a coarse chest capsule. This is not a guarantee against all
  hair/shoulder/clothing intersections; other styles and outfits need visual QA.
- Production use needs explicit per-rig binding, camera/distance policy and a
  browser performance budget. No WebGL or production deployment was performed.

## Focused checks

`HexLive.Tests.SecondaryBoneMotionTests`: moving chain at 30/60/120 Hz,
teleport/hitch reset, variable frame rate settling and disable/pose restoration.
Results are recorded after the native Unity test run; do not infer a pass from
this checklist alone.

## Verified solver tests

Native Unity PlayMode runner: **5/5 passed, exit 0**, 2026-10-11.
Receipt: `Build/MSIVisualAudit/SecondaryMotionTests/results.xml`; runner log beside it.

The first captures were rejected after frame review revealed that FinalIK
`fixTransforms` restored the startup limb pose after the manual animation graph.
The corrected disposable fixture disables its FinalIK components, measures actual
running foot excursion and asserts that no system overwrites the evaluated foot
pose before camera capture. Production prefab IK is unchanged. Never use the
rejected clips as evidence of a successful run/jump trial.


Leony-specific visual profile in the fixture: stiffness **360**, damping **30**,
max angle **7 degrees per joint**, root translation inheritance **0.9**. Its short
joints carry long cards, so the generic 14-degree setting lifted/curled the tail
too far in running views. The camera follows the jump vertically to preserve head
framing. These fixture settings do not modify the tested solver implementation.


## Final capture receipt

Final tuned native capture exited **0**: **420 frames / 14 seconds** at 30 Hz.
Measured maxima: hair **7.000 degrees**, breast **5.000 degrees**, spring length
error **1.1920929e-7 metres**. Running foot height excursion **0.5278 metres**;
maximum pose overwrite before rendering **0 metres**. At the final held pose,
breast deviation is zero and hair settles to about **1.086 degrees** (gravity/contact).
Jump head framing was checked over all 60 jump frames: minimum top margin **108 px**.
Selected front, jumping, rear-running and stopped frames were inspected.

Videos: `secondary-motion-ab.mp4` (normal speed, 14 s) and
`secondary-motion-detail-slow.mp4` (cropped, 2x slower), in the capture directory.
These demonstrate the prototype, not acceptance for every hair/outfit or live WebGL.
Source base: `8d2cae8e22a4faf6167e15fd3a9cd5f62247ad44` on `codex/msi-visual-bugs`.
