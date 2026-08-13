"""Regenerate Docs/WardrobePlacement.svg from committed BuildingRules values."""

from __future__ import annotations

import math
import re
from pathlib import Path


REPO = Path(__file__).resolve().parents[4]
RULES = REPO / "Assets/HexLive/Simulation/Runtime/BuildingRules.cs"
OUTPUT = REPO / "Docs/WardrobePlacement.svg"
INTERIOR_RADIUS = 3
BOUNDARY_RADIUS = 4
HEX_RADIUS = 1.5
SCALE = 110.0
CX = 340.0
CY = 300.0


def constant(source: str, name: str) -> float:
    match = re.search(
        rf"public const (?:float|int) {re.escape(name)} = (-?\d+(?:\.\d+)?)f?;",
        source,
    )
    if not match:
        raise ValueError(f"missing BuildingRules.{name}")
    return float(match.group(1))


def screen(point: tuple[float, float]) -> tuple[float, float]:
    return CX + point[0] * SCALE, CY - point[1] * SCALE


def interior_templates() -> dict[int, tuple[float, float]]:
    point_grid_radius = HEX_RADIUS / (BOUNDARY_RADIUS * math.sqrt(3.0))
    result: dict[int, tuple[float, float]] = {}
    slot = 0
    for axial_r in range(-INTERIOR_RADIUS, INTERIOR_RADIUS + 1):
        q_min = max(-INTERIOR_RADIUS, -axial_r - INTERIOR_RADIUS)
        q_max = min(INTERIOR_RADIUS, -axial_r + INTERIOR_RADIUS)
        for axial_q in range(q_min, q_max + 1):
            result[slot] = (
                point_grid_radius * 1.5 * axial_q,
                point_grid_radius * math.sqrt(3.0) * (axial_r + axial_q * 0.5),
            )
            slot += 1
    return result


def ring(radius: int) -> list[tuple[int, int]]:
    directions = ((1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1))
    q, r = -radius, radius
    result: list[tuple[int, int]] = []
    for dq, dr in directions:
        for _ in range(radius):
            result.append((q, r))
            q, r = q + dq, r + dr
    return result


def boundary_templates() -> list[tuple[float, float]]:
    point_grid_radius = HEX_RADIUS / (BOUNDARY_RADIUS * math.sqrt(3.0))
    return [
        (
            point_grid_radius * 1.5 * q,
            point_grid_radius * math.sqrt(3.0) * (r + q * 0.5),
        )
        for q, r in ring(BOUNDARY_RADIUS)
    ]


def bay_center(index: int) -> tuple[float, float]:
    edge, half = divmod(index, 2)
    a0 = math.radians(90.0 + edge * 60.0)
    a1 = math.radians(90.0 + (edge + 1) * 60.0)
    p0 = (math.cos(a0) * HEX_RADIUS, math.sin(a0) * HEX_RADIUS)
    p1 = (math.cos(a1) * HEX_RADIUS, math.sin(a1) * HEX_RADIUS)
    t = 0.25 if half == 0 else 0.75
    return p0[0] + (p1[0] - p0[0]) * t, p0[1] + (p1[1] - p0[1]) * t


def distance(a: tuple[float, float], b: tuple[float, float]) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def line(a: tuple[float, float], b: tuple[float, float], css: str) -> str:
    x1, y1 = screen(a)
    x2, y2 = screen(b)
    return f"<line x1='{x1:.1f}' y1='{y1:.1f}' x2='{x2:.1f}' y2='{y2:.1f}' class='{css}'/>"


def marker(point: tuple[float, float], css: str, radius: float = 3.0) -> str:
    x, y = screen(point)
    return f"<circle cx='{x:.1f}' cy='{y:.1f}' r='{radius:.1f}' class='{css}'/>"


def label(point: tuple[float, float], text: str, dy: float = -14.0) -> str:
    x, y = screen(point)
    return f"<text x='{x:.1f}' y='{y + dy:.1f}' text-anchor='middle'>{text}</text>"


def main() -> int:
    source = RULES.read_text()
    values = {
        name: constant(source, name)
        for name in (
            "HutBed0LocalX", "HutBed0LocalZ", "HutBed0LocalYaw",
            "HutBed1LocalX", "HutBed1LocalZ", "HutBed1LocalYaw",
            "HutHearthLocalX", "HutHearthLocalZ",
            "HutWardrobeLocalX", "HutWardrobeLocalZ", "HutWardrobeLocalYaw",
            "HutDoorBay",
        )
    }
    nodes = interior_templates()
    wardrobe = (values["HutWardrobeLocalX"], values["HutWardrobeLocalZ"])
    bed0 = (values["HutBed0LocalX"], values["HutBed0LocalZ"])
    bed1 = (values["HutBed1LocalX"], values["HutBed1LocalZ"])
    hearth = (values["HutHearthLocalX"], values["HutHearthLocalZ"])
    door = bay_center(int(values["HutDoorBay"]))
    footprint_slots = (9, 4, 0)

    hex_points = " ".join(
        f"{x:.1f},{y:.1f}"
        for x, y in (
            screen((math.cos(math.radians(90 + side * 60)) * HEX_RADIUS,
                    math.sin(math.radians(90 + side * 60)) * HEX_RADIUS))
            for side in range(6)
        )
    )
    parts = [
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 760 620' font-family='sans-serif'>",
        "<style>text{font-size:13px;fill:#252525}.small{font-size:12px;fill:#555}.title{font-size:17px;font-weight:600}.hex{fill:#fff;stroke:#8b7355;stroke-width:3}.grid{fill:#c9c9c9}.boundary{fill:#d9534f}.footprint{fill:#4fad74;stroke:#23683e;stroke-width:1.5}.axis{stroke:#4fad74;stroke-width:12;stroke-linecap:round;opacity:.48}.measure{stroke:#777;stroke-width:1.2;stroke-dasharray:4 3}.door{fill:#d6a82c;stroke:#6f5917;stroke-width:1.5}.bed{fill:#79a7dd;stroke:#2b547e;stroke-width:1.5}.hearth{fill:#e58d4b;stroke:#8d431f;stroke-width:1.5}</style>",
        "<rect width='760' height='620' fill='#fbfaf7'/>",
        "<text class='title' x='20' y='30'>Утверждённая раскладка гардероба — данные BuildingRules</text>",
        "<text class='small' x='20' y='51'>pointy-top hex R=1.5 wu; pivot junction 4; ось мебели junction 9 → 4 → 0</text>",
        f"<polygon points='{hex_points}' class='hex'/>",
    ]
    parts.extend(marker(point, "grid", 2.8) for point in nodes.values())
    parts.extend(marker(point, "boundary", 2.8) for point in boundary_templates())
    parts.append(line(nodes[9], nodes[0], "axis"))
    for slot in footprint_slots:
        parts.append(marker(nodes[slot], "footprint", 8.0 if slot == 4 else 6.0))
        parts.append(label(nodes[slot], str(slot), -12.0))
    parts.extend(
        (
            marker(bed0, "bed", 12.0), label(bed0, "кровать 0", -18.0),
            marker(bed1, "bed", 12.0), label(bed1, "кровать 1", -18.0),
            marker(hearth, "hearth", 12.0), label(hearth, "очаг", -18.0),
            marker(door, "door", 11.0), label(door, f"дверь / bay {int(values['HutDoorBay'])}", 29.0),
        )
    )
    for other in (bed0, hearth, door):
        parts.append(line(wardrobe, other, "measure"))
        midpoint = ((wardrobe[0] + other[0]) / 2, (wardrobe[1] + other[1]) / 2)
        parts.append(label(midpoint, f"{distance(wardrobe, other):.3f} wu", -6.0))
    parts.extend(
        (
            "<g transform='translate(535,105)'>",
            "<text font-weight='600'>Канонические данные</text>",
            f"<text class='small' y='27'>pivot: junction 4</text>",
            f"<text class='small' y='49'>X: {wardrobe[0]:.7f} wu</text>",
            f"<text class='small' y='71'>Z: {wardrobe[1]:.7f} wu</text>",
            f"<text class='small' y='93'>yaw: {values['HutWardrobeLocalYaw']:.0f}°</text>",
            "<text class='small' y='115'>occupied geometry: 9–4–0</text>",
            "<text class='small' y='137'>navigation obstacle: none</text>",
            "</g>",
            "<text class='small' x='20' y='576'>Модель: Blender +Z вверх, локальная +Y вдоль трёх точек; presentation-root без индивидуальной поправки.</text>",
            "<text class='small' x='20' y='598'>Схема генерируется скриптом skill из committed BuildingRules; PlayerPrefs не является production-источником.</text>",
            "</svg>",
        )
    )
    OUTPUT.write_text("\n".join(parts) + "\n")
    print(OUTPUT)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
