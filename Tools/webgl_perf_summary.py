#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Averages the `[WebPerf]` summaries of webgl_perf_probe.mjs runs (§168.11).

    python3 Tools/webgl_perf_summary.py <run.txt> [<run.txt> ...]

One column per run: fps, frame p50/p95, share of frames over 33 ms, GC
collections, set-pass calls, triangles, then the mean of every section row
(rows absent in a window count as 0 ms, i.e. under WebPerf's 0.2 ms cut).
"""

from __future__ import annotations

import pathlib
import re
import statistics
import sys

HEAD = re.compile(r"fps ([\d.]+)")
SPREAD = re.compile(r"frame p50 ([\d.]+) p95 ([\d.]+) ms\s+>33ms (\d+)/(\d+)\s+gc (\d+)")
DRAW = re.compile(r"setpass (\S+) tris (\S+)")
ROW = re.compile(r"^\s*([\d.]+) ms\s+\(max\s+(\d+)\)\s+(.+)$")


def parse(path: pathlib.Path) -> dict:
    windows = [w for w in path.read_text(encoding="utf-8").split("[WebPerf]")[1:] if HEAD.search(w)]
    result = {"windows": len(windows), "fps": [], "p50": [], "p95": [], "slow": 0, "frames": 0,
              "gc": 0, "setpass": [], "tris": [], "rows": {}}
    for window in windows:
        result["fps"].append(float(HEAD.search(window).group(1)))
        if m := SPREAD.search(window):
            result["p50"].append(float(m.group(1)))
            result["p95"].append(float(m.group(2)))
            result["slow"] += int(m.group(3))
            result["frames"] += int(m.group(4))
            result["gc"] += int(m.group(5))
        if m := DRAW.search(window):
            if m.group(1).isdigit():
                result["setpass"].append(int(m.group(1)))
            if m.group(2).isdigit():
                result["tris"].append(int(m.group(2)))
        for line in window.splitlines():
            if m := ROW.match(line):
                result["rows"].setdefault(m.group(3).strip(), []).append(float(m.group(1)))
    return result


def mean(values: list) -> float:
    return statistics.fmean(values) if values else 0.0


def main() -> int:
    runs = [(pathlib.Path(p).stem, parse(pathlib.Path(p))) for p in sys.argv[1:]]
    if not runs:
        print(__doc__)
        return 2
    width = max(10, *(len(name) for name, _ in runs))
    print(f"{'':42}" + "".join(f"{name:>{width}}" for name, _ in runs))

    def line(label: str, values: list[str]) -> None:
        print(f"{label:42}" + "".join(f"{v:>{width}}" for v in values))

    line("windows (1 s)", [str(r["windows"]) for _, r in runs])
    line("fps (mean)", [f"{mean(r['fps']):.1f}" for _, r in runs])
    line("frame p50 ms (mean of windows)", [f"{mean(r['p50']):.1f}" for _, r in runs])
    line("frame p95 ms (mean of windows)", [f"{mean(r['p95']):.1f}" for _, r in runs])
    line("frames > 33 ms", [f"{100 * r['slow'] / max(1, r['frames']):.1f}%" for _, r in runs])
    line("GC collections", [str(r["gc"]) for _, r in runs])
    line("set-pass calls", [f"{mean(r['setpass']):.0f}" for _, r in runs])
    line("triangles (M)", [f"{mean(r['tris']) / 1e6:.2f}" for _, r in runs])
    names = sorted({row for _, r in runs for row in r["rows"]},
                   key=lambda row: -max(sum(r["rows"].get(row, [])) / max(1, r["windows"]) for _, r in runs))
    print()
    for row in names:
        values = [sum(r["rows"].get(row, [])) / max(1, r["windows"]) for _, r in runs]
        if max(values) >= 0.2:
            line(row[:42], [f"{v:.2f}" for v in values])
    return 0


if __name__ == "__main__":
    sys.exit(main())
