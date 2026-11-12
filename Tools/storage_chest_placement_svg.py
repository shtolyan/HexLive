"""§151.6 r2 (#732): regenerate Docs/StorageChestPlacement.svg from code constants.

python Tools/storage_chest_placement_svg.py
"""

import math
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "Docs/StorageChestPlacement.svg"


def const(path, name, kind="float"):
    text = (ROOT / path).read_text(encoding="utf-8")
    pattern = rf"const {kind} {name} = ([\d.]+)f?;"
    return float(re.search(pattern, text)[1])


def main():
    chest = "Assets/HexLive/Simulation/Content/StorageChest.cs"
    radius = const("Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs", "HexRadius")
    length, width, height = (const(chest, n) for n in ("ModelLength", "ModelWidth", "ModelHeight"))
    guard, obstacle = const(chest, "FootprintRadius"), const(chest, "ObstacleRadius")
    capacity = 3 * int(const(chest, "BackpackCells", "int"))
    sticks, boards, rope = (int(const(chest, n, "int")) for n in ("BillSticks", "BillBoards", "BillRope"))
    collector_height, knee, waist = 1.046, .37, .79   # measured FBX, 1.32 wu body (see Spec/151.md)

    step_y = radius / 8            # junction key step: 0.1875 wu (lattice step 0.375)
    step_x = math.sqrt(3) * step_y
    apothem = radius * math.sqrt(3) / 2
    nodes = []
    for kx in range(-8, 9):
        for ky in range(-16, 17):
            if (kx + ky) % 2:
                continue
            x, y = kx * step_x, ky * step_y
            if abs(x) <= apothem + 1e-4 and abs(y) + abs(x) / math.sqrt(3) <= radius + 1e-4:
                nodes.append((kx, ky, x, y))
    disc = {(0, 0), (1, 1), (1, -1), (0, -2), (-1, -1), (-1, 1), (0, 2)}   # StationDisc keys

    scale, cx, cy = 140, 260, 285
    sx = lambda x: cx + x * scale
    sy = lambda y: cy - y * scale
    out = ['<svg xmlns="http://www.w3.org/2000/svg" width="900" height="600" viewBox="0 0 900 600">',
           '<rect width="900" height="600" fill="#13191f"/>',
           '<g font-family="Arial" fill="#e6edf3">',
           '<text x="30" y="40" font-size="22">Сундук в меню постройки (§151.6 r2 / #732)</text>']
    hexagon = " ".join(f"{sx(radius * math.cos(math.radians(90 + 60 * i))):.2f},"
                       f"{sy(radius * math.sin(math.radians(90 + 60 * i))):.2f}" for i in range(6))
    out.append(f'<polygon points="{hexagon}" fill="#1d2825" stroke="#82928a" stroke-width="1.5"/>')
    out.append(f'<circle cx="{cx}" cy="{cy}" r="{guard * scale:.2f}" fill="none" stroke="#93bf8c" '
               'stroke-dasharray="4 3"/>')
    out.append(f'<circle cx="{cx}" cy="{cy}" r="{obstacle * scale:.2f}" fill="#d0503c" fill-opacity=".12" '
               'stroke="#d0503c"/>')
    out.append(f'<rect x="{sx(-width / 2):.2f}" y="{sy(length / 2):.2f}" width="{width * scale:.2f}" '
               f'height="{length * scale:.2f}" rx="2" fill="#c59a68" fill-opacity=".55" stroke="#eed1a9"/>')
    ring = []
    for kx, ky, x, y in nodes:
        if (kx, ky) in disc:
            fill, r = "#e8873a", 4.5
        elif max(abs(kx), abs(ky) / 2) <= 2 and math.hypot(x, y) < .8:
            fill, r = "#5aa7e0", 3.5
            ring.append((x, y))
        else:
            fill, r = "#76818a", 3
        out.append(f'<circle cx="{sx(x):.2f}" cy="{sy(y):.2f}" r="{r}" fill="{fill}"/>')
    out.append(f'<circle cx="{cx}" cy="{cy}" r="6" fill="#dcf9cc" stroke="#385b3f"/>')
    out.append(f'<path d="M {cx} {sy(length / 2) - 8:.1f} V {sy(length / 2) - 40:.1f} M {cx - 6} '
               f'{sy(length / 2) - 32:.1f} L {cx} {sy(length / 2) - 42:.1f} L {cx + 6} '
               f'{sy(length / 2) - 32:.1f}" fill="none" stroke="#eed1a9" stroke-width="2"/>')
    out.append(f'<text x="{cx + 10}" y="{sy(length / 2) - 30:.1f}" font-size="12">+Y модели (длина)</text>')

    legend = [
        ("#c59a68", f"Корпус {width:.2f} × {length:.2f} wu, высота {height:.2f} wu"),
        ("#e8873a", "След: якорь + 6 соседей (как у водосборника), 7 узлов"),
        ("#d0503c", f"Блокировка навигации: радиус {obstacle:.2f} wu = те же 7 узлов"),
        ("#93bf8c", f"Защитный радиус с открытой крышкой {guard:.2f} wu"),
        ("#5aa7e0", f"Узлы подхода вокруг следа ({len(ring)})"),
        ("#76818a", f"Сетка 0.375 wu: {len(nodes)} узлов (37 внутр. + 24 на гранях)"),
    ]
    for i, (color, text) in enumerate(legend):
        y = 120 + 32 * i
        out.append(f'<circle cx="500" cy="{y}" r="5" fill="{color}"/>')
        out.append(f'<text x="514" y="{y + 5}" font-size="14">{text}</text>')
    out.append(f'<text x="500" y="{120 + 32 * len(legend) + 8}" font-size="14">'
               f'Вместимость {capacity} ячеек = 3 рюкзака × 9</text>')
    out.append(f'<text x="500" y="{120 + 32 * len(legend) + 30}" font-size="14">'
               f'Строится: {sticks} палки, {boards} досок, {rope} верёвки, без молотка</text>')

    # Side elevation: collector, chest, body reference.
    base, ez, ex = 560, 110, 500
    ey = lambda h: base - h * ez
    out.append(f'<line x1="{ex - 10}" y1="{base}" x2="{ex + 380}" y2="{base}" stroke="#82928a"/>')
    out.append(f'<rect x="{ex}" y="{ey(collector_height):.1f}" width="{.93 * ez:.1f}" '
               f'height="{collector_height * ez:.1f}" fill="none" stroke="#6f9b65"/>')
    out.append(f'<text x="{ex}" y="{ey(collector_height) - 6:.1f}" font-size="12">водосборник 0.93 × 1.05</text>')
    out.append(f'<rect x="{ex + 130}" y="{ey(height):.1f}" width="{length * ez:.1f}" '
               f'height="{height * ez:.1f}" fill="#c59a68" stroke="#eed1a9"/>')
    out.append(f'<text x="{ex + 130}" y="{ey(height) - 6:.1f}" font-size="12">сундук {height:.2f} (40 %)</text>')
    out.append(f'<rect x="{ex + 270}" y="{ey(1.32):.1f}" width="26" height="{1.32 * ez:.1f}" '
               'fill="#5c6f9a" fill-opacity=".6"/>')
    for h, label, color in ((knee, "колено ~0.37", "#d78273"), (waist, "пояс ~0.79", "#70bfdf")):
        out.append(f'<line x1="{ex - 10}" y1="{ey(h):.1f}" x2="{ex + 300}" y2="{ey(h):.1f}" '
                   f'stroke="{color}" stroke-dasharray="3 3"/>')
        out.append(f'<text x="{ex + 302}" y="{ey(h) + 4:.1f}" font-size="12">{label}</text>')
    out.append('<text x="30" y="585" font-size="13">Числа из StorageChest, HexSpatialMath, '
               'BlueprintFurnitureFootprints; генератор Tools/storage_chest_placement_svg.py.</text>')
    out += ["</g>", "</svg>", ""]
    OUT.write_text("\n".join(out), encoding="utf-8", newline="\n")
    print(f"wrote {OUT} nodes={len(nodes)} approach={len(ring)}")


if __name__ == "__main__":
    main()
