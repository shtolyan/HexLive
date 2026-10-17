#!/usr/bin/env python3
"""Read-only construction handoff checks. Never launches Unity or builds/publishes."""
import argparse
import hashlib
import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
MANIFEST = REPO / "Tools/construction_stage_manifest.json"


def source_contracts():
    contracts = {}
    for definition in json.loads(MANIFEST.read_text(encoding="utf-8"))["definitions"]:
        model = REPO / definition["export"]
        receipt = json.loads(model.with_suffix(".construction.json").read_text(encoding="utf-8"))
        if receipt.get("definitionId") != definition["id"] or receipt.get("version") != 1:
            raise ValueError(f"{model}: wrong construction contract")
        if receipt["modelSha256"] != hashlib.sha256(model.read_bytes()).hexdigest():
            raise ValueError(f"{model}: stale receipt / unsmudged LFS file")
        expected = [(stage["index"], stage["material"])
                    for stage in definition["stages"] for _ in range(stage["units"])]
        actions = receipt["actions"]
        if len(actions) != len(expected):
            raise ValueError(f"{model}: incorrect unit count")
        for index, (action, (stage, material)) in enumerate(zip(actions, expected), 1):
            if (action["index"], action["stage"], action["resource"], action["count"]) != (index, stage, material, 1):
                raise ValueError(f"{model}: invalid step {index}")
            if not action["nodePath"].startswith(definition["id"] + "/"):
                raise ValueError(f"{model}: invalid node path at step {index}")
        kind = "building" if definition["id"].startswith("architecture.") else "object"
        contracts[(kind, definition["id"])] = receipt
    return contracts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("verify-source", "plan", "verify-candidates"))
    parser.add_argument("--platform", choices=("StandaloneWindows64", "StandaloneOSX", "WebGL"),
                        default="StandaloneWindows64")
    parser.add_argument("--root", type=Path, default=REPO / "Build/AtomicContent")
    args = parser.parse_args()
    contracts = source_contracts()
    if args.command == "plan":
        for kind, identity in contracts:
            output = args.root / kind / identity / args.platform
            print(f'python Tools/content.py build --type {kind} --id {identity} '
                  f'--platform {args.platform} --output "{output}"')
    elif args.command == "verify-candidates":
        for (kind, identity), receipt in contracts.items():
            path = args.root / kind / identity / args.platform / "candidate.json"
            candidate = json.loads(path.read_text(encoding="utf-8"))
            if candidate.get("type") != kind or candidate.get("id") != identity:
                raise ValueError(f"{path}: wrong content owner")
            if candidate.get("metadata", {}).get("construction") != receipt:
                raise ValueError(f"{path}: old bundle / construction receipt mismatch")
            variants = [v for v in candidate["variants"] if v["platform"] == args.platform]
            if len(variants) != 1:
                raise ValueError(f"{path}: missing or duplicate platform")
            payload = path.parent / "payload.bundle"
            variant = variants[0]
            if (payload.stat().st_size != variant["size"] or
                    hashlib.sha256(payload.read_bytes()).hexdigest() != variant["sha256"]):
                raise ValueError(f"{path}: payload differs from candidate")
        print(f"PASS: {len(contracts)} current construction candidates for {args.platform}")
    else:
        print(f"PASS: {len(contracts)} models, "
              f"{sum(len(r['actions']) for r in contracts.values())} construction units; no builds performed")


if __name__ == "__main__":
    main()
