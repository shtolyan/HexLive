# #495 — ClipWindowGate on MSI

Player source `562a3b07d7fec9bfe6066ff66937aa68701656f6` was built first,
as requested. Canonical Unity6000.4.6f1 returned0; the release Player was
packaged, Editor target restored to Windows, and Unity closed before diagnosis.

## Evidence

- An isolated `EveryAttackClipFitsItsSimWindow` run was aborted by the diagnostic
  2-minute inactivity timer. The earlier full-suite5-minute timeout is recorded
  in `Docs/Bug493Verification.md`. Neither aborted run is a pass.
- A managed stack of the owned testhost showed `OSFileStreamStrategy..ctor`
  inside `UnityAsset.MetaByGuid`, before clip-length checks. No memory dump.
- A local replica of the sequential GUID-header scan, with per-file progress,
  read all24,232 metas in318.660s. It completed; the delay was accumulated file
  opening, including unrelated imported materials and audio sidecars, rather
  than an identified corrupt clip. No OS security setting was changed.
- Local diagnostic files: `Build/MSIRelease/20261019-master-release/clip-clip.*`,
  `clip-baseline-stack.txt`, and
  `Build/Diagnostics/20261019-clipwindow/meta-scan.log`.

## Change and coverage

The test helper still enumerates **all** metas under the supplied root, with
the same GUID regex. Eight bounded readers fill separate array slots; only
after they complete is the dictionary assembled in original enumeration order.
Duplicate-GUID last-entry behaviour is preserved. A root-keyed `Lazy` cache
prevents publication of a partial index and cross-root contamination.

`ClipWindowGateTests`, its fit thresholds, minimum clip count, Known ratchet,
clip parser, and related contact/fist gates are unchanged. No files or asset
types are excluded. File-read errors still fail index construction.

Focused native checks: **12/12 passed**, including actual ClipWindow,
StrikeContact, fist-presentation and gender-markup checks, plus new full-index,
cross-root clip-duration and concurrent-first-lookup regression tests. An initial
new-fixture run had Windows separator/YAML fixture errors; those were corrected,
and its log retained as `focused-fixture-errors.*`. The first optimized actual
ClipWindow run took0.682s. These timings are not a controlled cold-cache benchmark.

This change affects only native test infrastructure. Full clean-commit Release
suites and staged client/server compatibility checks remain required before
the separately authorized Singapore activation. Exact release receipts belong
in `RELEASE_COORDINATION_SVETA.md`; this document alone is not a deployment receipt.
