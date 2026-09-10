"""Derive required, measured garment profiles without re-running Unity.

The raw full authoring sweep remains immutable and failed on orphan metadata.
This derivative accepts only active/save-supported coverage, with explicit
orphan diagnostics, raw proof hash, cleanup and supported-pose checks.
"""
from pathlib import Path
import argparse
import hashlib
import json

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--input-dir", type=Path, default=Path(__file__).resolve().parent)
parser.add_argument("--output-dir", type=Path, default=None)
args = parser.parse_args()
ROOT = args.input_dir.resolve()
OUTPUT = (args.output_dir or ROOT).resolve()
OUTPUT.mkdir(parents=True, exist_ok=True)
raw_path = ROOT / "garment-ground-bounds.result.json"
raw_bytes = raw_path.read_bytes()
raw = json.loads(raw_bytes)
assert raw["busy"] is False and raw["stage"] == "complete"
cleanup = raw["cleanup"]
assert cleanup["succeeded"] is True
for key in ("cacheRestored", "libraryRestored", "scenesRestored", "noLoads", "persistentSourceDirtyFlagsUnchanged"):
    assert cleanup[key] is True, key
for key in ("liveOwnedGameObjects", "liveOwnedMeshes", "liveOwnedMaterials"):
    assert cleanup[key] == 0, key

required_sources = {
    "defaults", "active-at-entry", "simdata", "simdata-object-dress",
    "catalog-including-retired",
}
required = {}
orphans = {}
for key, row in raw["coverage"].items():
    if required_sources.intersection(row["sources"]):
        required[key] = row
    else:
        assert row["sources"] == ["authored-definition"], (key, row["sources"])
        orphans[key] = row
assert required
profiles = {}
for key, row in required.items():
    assert row["measured"] is True, key
    profile_id = row["profileId"]
    assert profile_id == row["prototypeId"], key
    profile = raw["profiles"][profile_id]
    assert profile["passed"] is True and profile["poses"], profile_id
    actual_branches = {pose["keepsShapeOnGround"] for pose in profile["poses"]}
    requested_branches = set(row.get("declaredKeepsShapeBranches", [False]))
    assert actual_branches.issuperset(requested_branches), (key, actual_branches, requested_branches)
    assert all(pose["passed"] and pose["sourceBonesRestored"] for pose in profile["poses"]), profile_id
    for axis in "xyz":
        expected_min = min(pose["scaledBounds"]["min"][axis] for pose in profile["poses"])
        expected_max = max(pose["scaledBounds"]["max"][axis] for pose in profile["poses"])
        assert abs(profile["scaledBounds"]["min"][axis] - expected_min) <= 1e-6, profile_id
        assert abs(profile["scaledBounds"]["max"][axis] - expected_max) <= 1e-6, profile_id
    profiles[profile_id] = profile

orphan_failures = []
for failure in raw["failures"]:
    key = failure.split(":", 1)[0]
    assert key in orphans, failure
    assert "must resolve to exactly one prefab" in failure and "found 0" in failure, failure
    orphan_failures.append({"definitionId": key, "failure": failure, "sources": orphans[key]["sources"]})

summary = {
    "proofVersion": 2,
    "runId": raw["runId"],
    "requiredCoveragePassed": True,
    "fullAuthoringSweepPassed": raw["passed"],
    "scope": "All active/save-supported garments, including retired definitions and Dress-only aliases. Authored-only orphan metadata is separately diagnostic; the raw full sweep is not rewritten as a pass.",
    "rawProof": {"path": str(raw_path), "sha256": hashlib.sha256(raw_bytes).hexdigest()},
    "fixtureSource": {"path": str(ROOT / "GarmentGroundBounds.execute-code.cs.txt"), "sha256": hashlib.sha256((ROOT / "GarmentGroundBounds.execute-code.cs.txt").read_bytes()).hexdigest()},
    "requiredSourceKinds": sorted(required_sources),
    "requiredDefinitions": len(required),
    "requiredMeasured": sum(row["measured"] for row in required.values()),
    "requiredRetiredDefinitions": sum(row["retired"] for row in required.values()),
    "profiles": len(profiles),
    "multiPoseProfiles": [key for key, profile in profiles.items() if len(profile["poses"]) > 1],
    "authoredOnlyOrphanDefinitions": len(orphans),
    "orphanFailures": orphan_failures,
    "elapsedSeconds": raw["elapsedSeconds"],
    "cleanup": cleanup,
    "consoleErrors": 0,
    "consoleEvidence": "Unity read_console(error) after final callback returned 0 entries before MCP lease release.",
    "limitations": "Authoring-source factory bounds only; no Asset API transport, shipped bundle, scene material screenshot or triangle-intersection certification. The profile union is conservative and retains each actually executed branch and its parameter provenance.",
}
out = {
    "summary": {key: value for key, value in summary.items() if key != "orphanFailures"},
    "coverage": required,
    "profiles": dict(sorted(profiles.items())),
    "sources": raw["sources"],
}
# Reproducible compact authoring input, independent of this machine's Build
# paths. The complete raw failure and pose/provenance detail remain above.
compact_summary = dict(out["summary"])
for key in ("rawProof", "fixtureSource"):
    compact_summary[key] = {"sha256": compact_summary[key]["sha256"]}
compact = {
    "summary": compact_summary,
    "profiles": {key: {field: value[field] for field in ("prototypeId", "scaledBounds")}
                 for key, value in out["profiles"].items()},
    "coverage": {key: {field: value[field] for field in ("measured", "profileId")}
                 for key, value in out["coverage"].items()},
    "sources": {key: {field: value[field] for field in ("sha256", "metaSha256") if field in value}
                for key, value in out["sources"].items()},
}
(OUTPUT / "GroundPileGarments.json").write_text(
    json.dumps(compact, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
(OUTPUT / "garment-ground-profiles.json").write_text(json.dumps(out, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
(OUTPUT / "garment-ground-summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({key: summary[key] for key in ("runId", "requiredCoveragePassed", "fullAuthoringSweepPassed", "requiredDefinitions", "requiredMeasured", "requiredRetiredDefinitions", "profiles", "multiPoseProfiles", "authoredOnlyOrphanDefinitions")}, ensure_ascii=False))
