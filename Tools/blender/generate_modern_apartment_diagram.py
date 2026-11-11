"""Draw the portal art footprint diagram from its measured FBX receipt."""
from __future__ import annotations

import json
import math
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "Assets/HexLiveContent/Portal/modern-apartment-art.json"
DESTINATION = ROOT / "Docs/ModernApartmentArt.svg"
NAMES = {
    "furniture.modern_bed": "Кровать",
    "furniture.modern_fridge": "Холодильник",
    "furniture.modern_table": "Стол",
    "furniture.modern_chair": "Стул",
    "furniture.modern_sofa": "Диван",
    "furniture.modern_tv": "Телевизор",
    "furniture.modern_shelf": "Стеллаж",
}


def diagram(receipt):
    radius = receipt["hexRadius"]
    apothem = receipt["hexApothem"]
    step = receipt["junctionStep"]
    corners = [(0, -radius), (apothem, -radius / 2), (apothem, radius / 2),
               (0, radius), (-apothem, radius / 2), (-apothem, -radius / 2)]
    rows = [row for row in receipt["records"] if row["id"] in NAMES]
    if len(rows) != len(NAMES):
        raise ValueError("Footprint diagram requires all measured furniture rows")
    architecture = {row["id"]: row for row in receipt["records"]}
    clear_width, clear_height = architecture["architecture.modern_opening"]["clearPassage"]
    door_width, _, door_height = architecture["architecture.modern_exit_jamb"]["wallAperture"]
    window_width, window_bottom, window_top = architecture["architecture.modern_window_jamb"]["wallAperture"]

    svg = [
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 920 598" role="img" '
        'aria-labelledby="title desc">',
        '<title id="title">Мебель и стыки современной квартиры</title>',
        '<desc id="desc">Гексы в натуральном масштабе, мебель на центральном узле, '
        'перегородки на общих рёбрах.</desc>',
        '<rect width="920" height="598" rx="14" fill="#f7f6ef"/>',
        '<g fill="#20292a" font-family="system-ui, sans-serif">',
        '<text x="30" y="36" font-size="24" font-weight="700">Мебель и стыки современной квартиры</text>',
        f'<text x="30" y="61" font-size="14">Гекс радиусом {radius:.2f} wu, '
        f'узлы с шагом {step:.3f} wu, размер по реальному FBX</text>',
    ]
    scale = 48
    for index, row in enumerate(rows):
        x, y = 81 + 126 * index, 165
        points = " ".join(f"{x + px * scale:.2f},{y - py * scale:.2f}" for px, py in corners)
        svg.append(f'<polygon points="{points}" fill="#e8eee9" stroke="#8b9c90"/>')
        for r in range(-3, 4):
            for q in range(max(-3, -r - 3), min(3, -r + 3) + 1):
                px = (step * math.sqrt(3) / 2) * q
                py = step * (r + q / 2)
                svg.append(f'<circle cx="{x + px * scale:.2f}" cy="{y - py * scale:.2f}" r="1.4" fill="#9cad9f"/>')
        low, high = row["blenderBounds"]["min"], row["blenderBounds"]["max"]
        width, length = high[0] - low[0], high[1] - low[1]
        svg.append(f'<rect x="{x + low[0] * scale:.2f}" y="{y - high[1] * scale:.2f}" '
                   f'width="{width * scale:.2f}" height="{length * scale:.2f}" '
                   'fill="#edb487" fill-opacity=".73" stroke="#b76238"/>')
        svg.append(f'<path d="M{x} {y + 30}V{y - 45} l-3 5 m3 -5 l3 5" stroke="#2c6783" '
                   'fill="none" stroke-width="1.4"/>')
        svg.append(f'<circle cx="{x}" cy="{y}" r="3" fill="#172d3c"/>')
        svg.append(f'<text x="{x}" y="253" text-anchor="middle" font-size="14" font-weight="600">'
                   f'{escape(NAMES[row["id"]])}</text>')
        svg.append(f'<text x="{x}" y="272" text-anchor="middle" font-size="12" fill="#4c5755">'
                   f'{width:.2f} × {length:.2f} wu</text>')

    x, y = 183, 462
    style_colors = ("#d8bd92", "#9eb6c9", "#a9bfad")
    for tile, color in zip(((0, 0), (1, 0), (0, 1)), style_colors):
        q, r = tile
        cx = 2 * apothem * (q + r / 2)
        cy = 1.5 * radius * r
        points = " ".join(f"{x + (px + cx) * scale:.2f},{y - (py + cy) * scale:.2f}"
                          for px, py in corners)
        svg.append(f'<polygon points="{points}" fill="{color}" stroke="#495d62" stroke-width="2"/>')
    edge_x = x + apothem * scale
    gap_half = clear_width * scale / 2
    edge_half = radius * scale / 2
    svg.extend([
        f'<path d="M{edge_x:.2f} {y - edge_half:.2f}V{y - gap_half:.2f} '
        f'M{edge_x:.2f} {y + gap_half:.2f}V{y + edge_half:.2f}" stroke="#62392b" stroke-width="6"/>',
        f'<path d="M{x + 35} {y}H{x + 95} l-7 -5 m7 5 l-7 5" stroke="#2c6783" fill="none" stroke-width="3"/>',
        '<text x="393" y="337" font-size="17" font-weight="650">Стыки комнат</text>',
        f'<text x="393" y="361" font-size="14">Плитки стыкуются по ребру {radius:.2f} wu.</text>',
        f'<text x="393" y="385" font-size="14">Открытая дверь оставляет проход {clear_width:.3f} × {clear_height:.3f} wu.</text>',
        f'<text x="393" y="409" font-size="14">Возвратная дверь занимает проём {door_width:.2f} × {door_height:.2f} wu.</text>',
        f'<text x="393" y="433" font-size="14">Окно с городом установлено в проём {window_width:.3f} × {window_top - window_bottom:.2f} wu.</text>',
        '<text x="393" y="457" font-size="14">Точка отмечает опору модели, синий штрих — ось +Y.</text>',
        '<text x="30" y="565" font-size="12" fill="#4c5755">Три пары отделки пола и стен '
        'согласованы по размеру, соседние комнаты могут иметь разные материалы.</text>',
        '</g></svg>',
    ])
    return "\n".join(svg) + "\n"


if __name__ == "__main__":
    DESTINATION.write_text(diagram(json.loads(CATALOG.read_text(encoding="utf-8"))),
                           encoding="utf-8", newline="\n")

