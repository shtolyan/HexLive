#!/usr/bin/env python3
"""Summarize one untouched acceptance matrix; never select the best rerun."""
import argparse
import json
from pathlib import Path

SCENARIOS = ("coconuts", "gift", "bed", "resilience")
CASES = {(fixture, repetition) for fixture in range(3) for repetition in range(2)}


def summarize(directories):
    groups = {}
    providers = set()
    for directory in directories:
        for path in sorted(Path(directory).glob("*.json")):
            data = json.loads(path.read_text())
            if not isinstance(data, dict) or data.get("scenario") not in SCENARIOS:
                continue
            if data.get("providerName") == "Replay":
                continue  # Recorded choices reproduce a failure; they are not model acceptance.
            provider = (data["providerName"], data["modelId"], data.get("reasoningEffort") or "unspecified")
            key = (*provider, data["scenario"])
            case = (data["fixture"], data["repetition"])
            if case not in CASES or any(type(v) is not int for v in case):
                raise ValueError(f"Invalid case in {path.name}")
            cases = groups.setdefault(key, {})
            if case in cases:
                raise ValueError(f"Duplicate case {key} {case}; keep reruns in separate summaries")
            cases[case] = data
            providers.add(provider)
    rows = []
    for provider in sorted(providers):
        for scenario in SCENARIOS:
            cases = groups.get((*provider, scenario), {})
            passed = sum(d.get("passed") is True and d.get("status") == "Completed" for d in cases.values())
            missing = sorted(CASES - cases.keys())
            rows.append(dict(provider=provider[0], model=provider[1], reasoningEffort=provider[2], scenario=scenario,
                             passed=passed, total=6, missing=missing,
                             accepted=not missing and passed >= 5 and not any(
                                 "FalseCompletion" in str(d.get("status", "")) for d in cases.values()),
                             calls=sum(d.get("calls", 0) for d in cases.values()),
                             commands=sum(d.get("commands", 0) for d in cases.values()),
                             failures=[dict(fixture=c[0], repetition=c[1], status=d.get("status", "Unknown"))
                                       for c, d in sorted(cases.items())
                                       if not (d.get("passed") is True and d.get("status") == "Completed")]))
    return dict(accepted=bool(rows) and all(r["accepted"] for r in rows), rows=rows,
                scope="Four model scenarios; resilience injects a lost acknowledgement. Live threat, dialogue and explicit cancellation also require separate runtime checks.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", nargs="+", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if any(not p.is_dir() for p in args.directories):
        parser.error("Every input must be an existing matrix directory")
    result = summarize(args.directories)
    text = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.write_text(text)
    print(text, end="")
    return 0 if result["accepted"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
