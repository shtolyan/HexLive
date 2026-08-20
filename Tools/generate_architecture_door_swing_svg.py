#!/usr/bin/env python3
"""Generate the six-yaw §120/#181 door-swing contract diagram from code constants."""

from __future__ import annotations

import math
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "Docs" / "Architecture" / "door-outward-six-yaws.svg"


def constant(path: Path, pattern: str, cast):
    match = re.search(pattern, path.read_text(encoding="utf-8"))
    if not match:
        raise RuntimeError(f"constant not found in {path}: {pattern}")
    return cast(match.group(1))


radius = constant(
    ROOT / "Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs",
    r"HexRadius\s*=\s*([0-9.]+)f", float)
door_bay = constant(
    ROOT / "Assets/HexLive/Simulation/Runtime/BuildingRules.cs",
    r"HutDoorBay\s*=\s*(\d+)", int)
open_degrees = constant(
    ROOT / "Tools/blender/build_arch_elements.py",
    r"DOOR_OPEN_DEGREES\s*=\s*(-?[0-9.]+)", float)


def rotate(point: tuple[float, float], degrees: float) -> tuple[float, float]:
    angle = math.radians(degrees)
    c, s = math.cos(angle), math.sin(angle)
    return point[0] * c - point[1] * s, point[0] * s + point[1] * c


def add(a: tuple[float, float], b: tuple[float, float]) -> tuple[float, float]:
    return a[0] + b[0], a[1] + b[1]


def scale(a: tuple[float, float], factor: float) -> tuple[float, float]:
    return a[0] * factor, a[1] * factor


edge = door_bay // 2
half = door_bay % 2
a0 = math.radians(90 + edge * 60)
a1 = math.radians(90 + (edge + 1) * 60)
a = math.cos(a0) * radius, math.sin(a0) * radius
b = math.cos(a1) * radius, math.sin(a1) * radius
t = 0.25 if half == 0 else 0.75
midpoint = add(a, scale((b[0] - a[0], b[1] - a[1]), t))
tangent_raw = b[0] - a[0], b[1] - a[1]
tangent_length = math.hypot(*tangent_raw)
tangent = scale(tangent_raw, 1 / tangent_length)
hinge = add(midpoint, scale(tangent, -0.25))
closed_tip = add(hinge, scale(tangent, 0.50))
open_tip = add(hinge, scale(rotate(tangent, open_degrees), 0.50))
rejected_tip = add(hinge, scale(rotate(tangent, -open_degrees), 0.50))

width, height = 1080, 760
panel_w, panel_h = 340, 285
origin_x, origin_y = 190, 190
world_scale = 72


def screen(point: tuple[float, float], yaw: float, cx: float, cy: float) -> tuple[float, float]:
    x, z = rotate(point, yaw)
    return cx + x * world_scale, cy - z * world_scale


parts = [
    f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
    '<defs><marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 z" fill="#45d483"/></marker></defs>',
    '<rect width="100%" height="100%" fill="#11171a"/>',
    '<text x="40" y="44" fill="#f3f5f4" font-family="sans-serif" font-size="24" font-weight="700">#181 — дверь открывается наружу во всех 6 yaw</text>',
    f'<text x="40" y="72" fill="#9fb0b6" font-family="sans-serif" font-size="15">R={radius:.1f} wu · bay={door_bay} · authored swing={open_degrees:.0f}° · yaw=0°+60°k</text>',
]

hex_points = [(math.cos(math.radians(90 + i * 60)) * radius,
               math.sin(math.radians(90 + i * 60)) * radius) for i in range(6)]

for step in range(6):
    row, col = divmod(step, 3)
    cx = origin_x + col * panel_w
    cy = origin_y + row * panel_h
    yaw = step * 60
    polygon = " ".join(f"{x:.1f},{y:.1f}" for x, y in
                       (screen(point, yaw, cx, cy) for point in hex_points))
    parts.append(f'<polygon points="{polygon}" fill="#1d2a2d" stroke="#66777c" stroke-width="2"/>')

    door_a = screen(add(midpoint, scale(tangent, -0.25)), yaw, cx, cy)
    door_b = screen(add(midpoint, scale(tangent, 0.25)), yaw, cx, cy)
    h = screen(hinge, yaw, cx, cy)
    c = screen(closed_tip, yaw, cx, cy)
    o = screen(open_tip, yaw, cx, cy)
    bad = screen(rejected_tip, yaw, cx, cy)
    outward_tip = screen(add(midpoint, scale(midpoint, 0.36 / math.hypot(*midpoint))), yaw, cx, cy)

    parts.extend([
        f'<line x1="{door_a[0]:.1f}" y1="{door_a[1]:.1f}" x2="{door_b[0]:.1f}" y2="{door_b[1]:.1f}" stroke="#f4d35e" stroke-width="7"/>',
        f'<line x1="{h[0]:.1f}" y1="{h[1]:.1f}" x2="{c[0]:.1f}" y2="{c[1]:.1f}" stroke="#a9b6ba" stroke-width="4" stroke-dasharray="7 5"/>',
        f'<line x1="{h[0]:.1f}" y1="{h[1]:.1f}" x2="{bad[0]:.1f}" y2="{bad[1]:.1f}" stroke="#d95d5d" stroke-width="3" opacity="0.55"/>',
        f'<line x1="{h[0]:.1f}" y1="{h[1]:.1f}" x2="{o[0]:.1f}" y2="{o[1]:.1f}" stroke="#45d483" stroke-width="6"/>',
        f'<line x1="{screen(midpoint,yaw,cx,cy)[0]:.1f}" y1="{screen(midpoint,yaw,cx,cy)[1]:.1f}" x2="{outward_tip[0]:.1f}" y2="{outward_tip[1]:.1f}" stroke="#45d483" stroke-width="2" marker-end="url(#arrow)"/>',
        f'<circle cx="{h[0]:.1f}" cy="{h[1]:.1f}" r="5" fill="#f4d35e"/>',
        f'<text x="{cx-145:.1f}" y="{cy-122:.1f}" fill="#f3f5f4" font-family="sans-serif" font-size="17" font-weight="700">yaw {yaw}°</text>',
    ])

parts.extend([
    '<line x1="48" y1="690" x2="88" y2="690" stroke="#45d483" stroke-width="6"/><text x="98" y="696" fill="#cdd7da" font-family="sans-serif" font-size="15">новая открытая поза: наружу</text>',
    '<line x1="360" y1="690" x2="400" y2="690" stroke="#d95d5d" stroke-width="3"/><text x="410" y="696" fill="#cdd7da" font-family="sans-serif" font-size="15">старая +72°: внутрь</text>',
    '<line x1="650" y1="690" x2="690" y2="690" stroke="#a9b6ba" stroke-width="4" stroke-dasharray="7 5"/><text x="700" y="696" fill="#cdd7da" font-family="sans-serif" font-size="15">закрытая створка</text>',
    '<text x="40" y="732" fill="#829399" font-family="sans-serif" font-size="13">Жёлтый: authored half-bay; точка: HL_Door_Pivot. Все координаты вычислены из HexRadius, HutDoorBay и DOOR_OPEN_DEGREES.</text>',
    '</svg>',
])

OUTPUT.parent.mkdir(parents=True, exist_ok=True)
OUTPUT.write_text("\n".join(parts) + "\n", encoding="utf-8")
print(OUTPUT)
