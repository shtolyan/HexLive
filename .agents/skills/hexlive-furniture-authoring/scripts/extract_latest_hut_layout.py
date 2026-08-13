"""Extract the last explicit HutLayoutDesigner save from Unity Editor.log.

The designer prints coordinates rounded for readability. This tool resolves the
selected junction through the same mathematical grid as HexPointLayout so an
agent does not turn display rounding into new production geometry.
"""

from __future__ import annotations

import argparse
import json
import math
import re
from pathlib import Path


MARKER = "[HutDesigner][SAVED]"
INTERIOR_RADIUS = 3
BOUNDARY_RADIUS = 4
HEX_RADIUS = 1.5


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--log",
        type=Path,
        default=Path.home() / "Library/Logs/Unity/Editor.log",
    )
    parser.add_argument(
        "--type",
        dest="furniture_type",
        help="Emit one approved furniture row (for example: wardrobe).",
    )
    return parser.parse_args()


def interior_templates() -> dict[int, tuple[float, float]]:
    point_grid_radius = HEX_RADIUS / (BOUNDARY_RADIUS * math.sqrt(3.0))
    templates: dict[int, tuple[float, float]] = {}
    slot = 0
    for axial_r in range(-INTERIOR_RADIUS, INTERIOR_RADIUS + 1):
        q_min = max(-INTERIOR_RADIUS, -axial_r - INTERIOR_RADIUS)
        q_max = min(INTERIOR_RADIUS, -axial_r + INTERIOR_RADIUS)
        for axial_q in range(q_min, q_max + 1):
            local_x = point_grid_radius * 1.5 * axial_q
            local_z = point_grid_radius * math.sqrt(3.0) * (
                axial_r + axial_q * 0.5
            )
            templates[slot] = (local_x, local_z)
            slot += 1
    return templates


def latest_payload(log_text: str) -> dict:
    markers = list(re.finditer(re.escape(MARKER), log_text))
    if not markers:
        raise ValueError(f"no {MARKER} payload found")
    start = log_text.find("{", markers[-1].end())
    if start < 0:
        raise ValueError("latest save marker has no JSON payload")
    payload, _ = json.JSONDecoder().raw_decode(log_text[start:])
    return payload


def resolved_item(item: dict, templates: dict[int, tuple[float, float]]) -> dict:
    slot = int(item["junction"])
    if slot not in templates:
        raise ValueError(f"junction {slot} is outside the interior template")
    local_x, local_z = templates[slot]
    return {
        "type": item["type"],
        "junction": slot,
        "savedLocalX": item.get("localX"),
        "savedLocalZ": item.get("localZ"),
        "localX": local_x,
        "localZ": local_z,
        "rotationDegrees": item.get("rotationDegrees", 0),
    }


def main() -> int:
    args = parse_args()
    payload = latest_payload(args.log.expanduser().read_text(errors="replace"))
    templates = interior_templates()
    furniture = [resolved_item(item, templates) for item in payload.get("furniture", [])]

    if args.furniture_type:
        matches = [item for item in furniture if item["type"] == args.furniture_type]
        if len(matches) != 1:
            raise ValueError(
                f"expected exactly one {args.furniture_type!r}, found {len(matches)}; "
                "promote only an explicitly approved instance"
            )
        output: object = matches[0]
    else:
        output = {
            "version": payload.get("version"),
            "bays": payload.get("bays", []),
            "furniture": furniture,
        }

    print(json.dumps(output, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
