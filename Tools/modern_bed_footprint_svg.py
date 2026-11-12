"""Draw Docs/ModernBedFootprint.svg from the committed constants (#755-757).

    python3 Tools/modern_bed_footprint_svg.py

One hex (HexSpatialMath.HexRadius), its 61 lattice junctions (37 interior +
24 on the edges, 0.375 wu step), a wall on the left edge, and a bed in the
hut slot (BuiltInBuildingBlueprints: centre -0.974 wu, yaw 3): bed.basic's 14
footprint junctions, the 0.1.121 modern bed outline, the new modern bed and
the rendered bed.basic outline for comparison. Every number is read from C#.
"""
from __future__ import annotations

import math
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def const(path, name):
    text = (ROOT / path).read_text(encoding="utf-8")
    return float(re.search(rf"const float {name} = ([\d.]+)f;", text)[1])


R = const("Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs", "HexRadius")
SQRT3 = const("Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs", "Sqrt3")
APOTHEM = SQRT3 * R / 2
STEP_X, STEP_Y = SQRT3 * R / 8, R / 8           # FurniturePlacement.KeyStepX / KeyStepY
BED_W = const("Assets/HexLive/Simulation/Content/ModernLootCatalog.cs", "BedWidth")
BED_L = const("Assets/HexLive/Simulation/Content/ModernLootCatalog.cs", "BedLength")
WIDTH_SCALE = const("Assets/HexLive/UnityPresentation/Environment/BedAssembly.cs", "BedWidthScale")
LENGTH_SCALE = const("Assets/HexLive/UnityPresentation/Environment/BedAssembly.cs", "BedLengthScale")
BASIC_MESH = (0.5395, 1.3573)                    # bed_basic_final_native.fbx, measured in Blender
OLD_MODERN = (0.92, 1.829)                       # furniture.modern_bed.fbx in 0.1.121
WOOD_BOARD_HALF = 0.059                          # architecture.wall.wood boards, measured
MODERN_WALL_HALF = 0.0475                        # author_modern_apartment.py WALL_DEPTH / 2

# BlueprintFurnitureFootprints.BuildBed, yaw 0: rails x=+-1 at odd y -3..5,
# centre column at even y -2..4. Yaw 3 turns it 180 degrees.
LOCAL = [(x, y) for y in range(-3, 6, 2) for x in (-1, 1)] + [(0, y) for y in range(-2, 5, 2)]
CENTROID_LOCAL = (sum(x for x, _ in LOCAL) / len(LOCAL), sum(y for _, y in LOCAL) / len(LOCAL))
CENTRE = (-(APOTHEM - STEP_X), 0.0)               # hut slot / PortalApartmentLayout.BedAlongWall
ANCHOR = (CENTRE[0] + CENTROID_LOCAL[0] * STEP_X, CENTRE[1] + CENTROID_LOCAL[1] * STEP_Y)
FOOT = [(ANCHOR[0] - x * STEP_X, ANCHOR[1] - y * STEP_Y) for x, y in LOCAL]

SCALE, PAD = 230.0, 40.0
SIZE = 2 * (R + 0.25) * SCALE


def px(point):
    return PAD + (point[0] + R + 0.25) * SCALE, PAD + (R + 0.25 - point[1]) * SCALE


def hex_corners(radius=R):
    return [(radius * math.cos(math.radians(90 + 60 * i)), radius * math.sin(math.radians(90 + 60 * i)))
            for i in range(6)]


def inside(point, apothem=APOTHEM, eps=1e-4):
    return all(abs(point[0] * math.cos(math.radians(a)) + point[1] * math.sin(math.radians(a))) <= apothem + eps
               for a in (0, 60, 120))


def lattice():
    points = []
    for x in range(-4, 5):
        for y in range(-8, 9):
            if (x + y) % 2 == 0 and inside((x * STEP_X, y * STEP_Y)):
                points.append((x * STEP_X, y * STEP_Y))
    return points


def rect(centre, width, length, **style):
    x0, y0 = px((centre[0] - width / 2, centre[1] + length / 2))
    attrs = " ".join(f'{k.replace("_", "-")}="{v}"' for k, v in style.items())
    return f'<rect x="{x0:.1f}" y="{y0:.1f}" width="{width * SCALE:.1f}" height="{length * SCALE:.1f}" {attrs}/>'


def main():
    grid = lattice()
    assert len(grid) == 61, len(grid)
    assert all(any(math.dist(f, g) < 1e-4 for g in grid) for f in FOOT), "footprint off the lattice"
    corners = [(CENTRE[0] + sx * BED_W / 2, CENTRE[1] + sy * BED_L / 2) for sx in (-1, 1) for sy in (-1, 1)]
    hex_margin = min(APOTHEM - abs(c[0] * math.cos(math.radians(a)) + c[1] * math.sin(math.radians(a)))
                     for c in corners for a in (0, 60, 120))
    side_gap = (CENTRE[0] - BED_W / 2) + APOTHEM
    old_out = APOTHEM - (abs(CENTRE[0]) + OLD_MODERN[0] / 2)
    basic = (BASIC_MESH[0] * WIDTH_SCALE, BASIC_MESH[1] * LENGTH_SCALE)
    w = h = SIZE + 2 * PAD
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w + 430:.0f} {h:.0f}" font-family="sans-serif" font-size="14">',
           f'<rect width="{w + 430:.0f}" height="{h:.0f}" fill="#fbfaf6"/>']
    poly = " ".join(f"{px(c)[0]:.1f},{px(c)[1]:.1f}" for c in hex_corners())
    out.append(f'<polygon points="{poly}" fill="#efe8d8" stroke="#8a7d66" stroke-width="2"/>')
    a, b = hex_corners()[1], hex_corners()[2]       # the left (x = -apothem) edge
    for half, colour in ((WOOD_BOARD_HALF, "#b98b57"), (MODERN_WALL_HALF, "#c9b9a0")):
        x0 = px((-APOTHEM - half, 0))[0]
        out.append(f'<rect x="{x0:.1f}" y="{px(a)[1]:.1f}" width="{2 * half * SCALE:.1f}" '
                   f'height="{(a[1] - b[1]) * SCALE:.1f}" fill="{colour}" opacity=".55"/>')
    for g in grid:
        x, y = px(g)
        out.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="3.5" fill="#b9b2a4"/>')
    out.append(rect(CENTRE, OLD_MODERN[0], OLD_MODERN[1], fill="none", stroke="#d23c3c",
                    stroke_width="2.5", stroke_dasharray="8 5"))
    out.append(rect(CENTRE, basic[0], basic[1], fill="none", stroke="#7a5a2a", stroke_width="2",
                    stroke_dasharray="3 4"))
    out.append(rect(CENTRE, BED_W, BED_L, fill="#5f9e8c", fill_opacity=".35", stroke="#2f6f5e", stroke_width="2.5"))
    for f in FOOT:
        x, y = px(f)
        out.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="6" fill="#e07020" stroke="#7a3500"/>')
    ax, ay = px(ANCHOR)
    out.append(f'<circle cx="{ax:.1f}" cy="{ay:.1f}" r="10" fill="none" stroke="#1f4fb4" stroke-width="2.5"/>')
    cx, cy = px(CENTRE)
    out.append(f'<path d="M{cx - 7:.1f},{cy:.1f}h14M{cx:.1f},{cy - 7:.1f}v14" stroke="#1f4fb4" stroke-width="2"/>')
    hx, hy = px((CENTRE[0], CENTRE[1] + BED_L / 2))
    out.append(f'<text x="{hx:.1f}" y="{hy - 8:.1f}" text-anchor="middle" fill="#2f6f5e">изголовье</text>')
    lx = w + 10
    legend = [
        ("#b9b2a4", "узлы гекса: 61 (37 внутри + 24 на рёбрах), шаг 0,375 wu"),
        ("#e07020", "след bed.basic = след новой кровати: 14 узлов"),
        ("#1f4fb4", f"якорь (круг) и центр следа (крест), сдвиг {CENTROID_LOCAL[1] * STEP_Y:.4f} wu"),
        ("#2f6f5e", f"новая кровать {BED_W:.3f} × {BED_L:.3f} wu"),
        ("#7a5a2a", f"bed.basic на экране {basic[0]:.3f} × {basic[1]:.3f} wu"),
        ("#d23c3c", f"кровать 0.1.121: {OLD_MODERN[0]:.2f} × {OLD_MODERN[1]:.3f} wu"),
        ("#b98b57", f"доски деревянной стены ±{WOOD_BOARD_HALF:.3f} wu"),
        ("#c9b9a0", f"стена квартиры ±{MODERN_WALL_HALF:.4f} wu"),
    ]
    out.append(f'<text x="{lx}" y="{PAD + 4}" font-weight="bold" font-size="16">Современная кровать в слоте у стены</text>')
    for i, (colour, label) in enumerate(legend):
        y = PAD + 36 + 26 * i
        out.append(f'<rect x="{lx}" y="{y - 11}" width="16" height="12" fill="{colour}"/>'
                   f'<text x="{lx + 24}" y="{y}">{label}</text>')
    facts = [
        f"R = {R} wu, апофема = {APOTHEM:.4f} wu",
        f"рельсы следа: {2 * STEP_X:.4f} wu, торцевые ряды: {8 * STEP_Y:.3f} wu",
        f"центр следа: {abs(CENTRE[0]):.4f} wu от центра гекса",
        f"бок кровати до линии ребра: {side_gap:.4f} wu",
        f"запас углов до границы гекса: {hex_margin:.4f} wu",
        f"старая кровать вылезала за ребро на {-old_out:.3f} wu",
        f"и была длиннее ребра ({OLD_MODERN[1]:.3f} > {R} wu)",
    ]
    for i, line in enumerate(facts):
        out.append(f'<text x="{lx}" y="{PAD + 260 + 22 * i}">{line}</text>')
    out.append("</svg>")
    target = ROOT / "Docs/ModernBedFootprint.svg"
    target.write_text("\n".join(out) + "\n", encoding="utf-8", newline="\n")
    print(target, f"side_gap={side_gap:.4f} hex_margin={hex_margin:.4f} old_out={old_out:.4f}")


if __name__ == "__main__":
    main()
